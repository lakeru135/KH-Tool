<#
.SYNOPSIS
  Live round trip of KH-Tool's AI Bridge through KhMcp.exe — the same stdio path Claude Code uses.

.DESCRIPTION
  1. KhMcp.exe --schemas (no Revit needed).
  2. Starts Revit 2026 with KHMCP_AUTOSTART=1 when it is not running (the bridge is otherwise off
     at every start), then waits until KhMcp.exe --selftest reaches the bridge.
  3. With -Model: copies the model to %TEMP%\KhMcpVerify (the original is never opened), opens the
     copy through open_model, makes ONE undoable AI change (single_font), and checks the undo tool:
     ai_steps gains "AI: Single Font", undo removes it, and a second undo is refused.

  Dev rig only (not shipped). A freshly deployed unsigned dll makes Revit ask "Security - Unsigned
  Add-In" at start; answer it, or the wait times out.

.EXAMPLE
  .\Verify-KhMcp.ps1 -Model "C:\Program Files\Autodesk\Revit 2026\Samples\Snowdon Towers Sample Architectural.rvt"
#>
param(
    [string]$Model = "",
    [string]$Server = "$env:APPDATA\Autodesk\Revit\Addins\2026\KhMcp\KhMcp.exe",
    [int]$StartTimeoutSec = 360
)

$ErrorActionPreference = "Stop"
$failures = New-Object System.Collections.Generic.List[string]
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  PASS  $what" -ForegroundColor Green }
    else { Write-Host "  FAIL  $what" -ForegroundColor Red; $failures.Add($what) }
}

# One MCP session per call: initialize, then tools/call; returns the parsed JSON result text.
function Invoke-Tool([string]$name, [hashtable]$arguments = @{}) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $Server
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $p = [Diagnostics.Process]::Start($psi)
    $send = { param($o) $p.StandardInput.WriteLine(($o | ConvertTo-Json -Depth 10 -Compress)); $p.StandardInput.Flush() }
    $read = { param($id) while ($true) { $m = $p.StandardOutput.ReadLine() | ConvertFrom-Json; if ($m.id -eq $id) { return $m } } }
    & $send @{ jsonrpc = "2.0"; id = 1; method = "initialize"; params = @{ protocolVersion = "2025-06-18"; capabilities = @{}; clientInfo = @{ name = "Verify-KhMcp"; version = "1" } } }
    & $read 1 | Out-Null
    & $send @{ jsonrpc = "2.0"; method = "notifications/initialized" }
    & $send @{ jsonrpc = "2.0"; id = 2; method = "tools/call"; params = @{ name = $name; arguments = $arguments } }
    $m = & $read 2
    # KhMcp.exe ends on its own when its input closes (end of the MCP session).
    $p.StandardInput.Close(); $p.WaitForExit(5000) | Out-Null
    $text = ($m.result.content | Where-Object { $_.type -eq "text" } | Select-Object -First 1).text
    [pscustomobject]@{ IsError = [bool]$m.result.isError; Text = $text; Json = $(try { $text | ConvertFrom-Json } catch { $null }) }
}

Write-Host "== KhMcp.exe --schemas" -ForegroundColor Cyan
if (-not (Test-Path $Server)) { throw "No KhMcp.exe at $Server - deploy KH-Tool first (Install-Dev.ps1)." }
& $Server --schemas
Check ($LASTEXITCODE -eq 0) "every tool schema parses"

Write-Host "== bridge" -ForegroundColor Cyan
if (-not (Get-Process Revit -ErrorAction SilentlyContinue)) {
    $env:KHMCP_AUTOSTART = "1"
    $env:KHMCP_CODE = "1"
    Start-Process "C:\Program Files\Autodesk\Revit 2026\Revit.exe"
    Write-Host "  started Revit 2026 with the AI Bridge and AI Code on"
}
$deadline = (Get-Date).AddSeconds($StartTimeoutSec)
do {
    & $Server --selftest *> $null
    if ($LASTEXITCODE -eq 0) { break }
    Start-Sleep 4
} while ((Get-Date) -lt $deadline)
Check ($LASTEXITCODE -eq 0) "KhMcp.exe reaches the AI Bridge (if Revit was already open, turn AI Bridge ON)"
if ($LASTEXITCODE -ne 0) { exit 1 }

$status = Invoke-Tool "kh_status"
Check (-not $status.IsError -and $status.Json.tools.Count -gt 0) "kh_status answers ($($status.Json.tools.Count) tools)"

if ($Model) {
    Write-Host "== undo round trip on a copy of $(Split-Path $Model -Leaf)" -ForegroundColor Cyan
    $dir = Join-Path $env:TEMP "KhMcpVerify"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $copy = Join-Path $dir (Split-Path $Model -Leaf)
    Copy-Item $Model $copy -Force
    Set-ItemProperty $copy -Name IsReadOnly -Value $false

    $open = Invoke-Tool "open_model" @{ path = $copy }
    Check (-not $open.IsError -and $open.Json.model.path -eq $copy) "open_model opens the copy$(if ($open.Json.dialogs_answered) { ' (answered: ' + ($open.Json.dialogs_answered -join '; ') + ')' })"

    $steps = { (Invoke-Tool "kh_status").Json.model.ai_steps }
    $before = @(& $steps).Count
    $font = Invoke-Tool "single_font" @{ font = "Arial" }
    Check (-not $font.IsError) "single_font runs ($($font.Json.summary | ConvertTo-Json -Compress))"
    $after = @(& $steps)
    Check ($after.Count -eq $before + 1 -and $after[0] -eq "AI: Single Font") "the call is recorded as one AI step"

    $undo = Invoke-Tool "undo"
    Check (-not $undo.IsError) "undo is posted"
    Start-Sleep 3
    Check (@(& $steps).Count -eq $before) "undo took the AI step back"

    if ($before -eq 0) {
        $again = Invoke-Tool "undo"
        Check ($again.IsError) "a further undo is refused (nothing of the AI's left)"
    }

    Write-Host "== run_code (AI Code must be ON)" -ForegroundColor Cyan
    $read = Invoke-Tool "run_code" @{ code = "log.WriteLine(app.Application.VersionNumber);`nreturn new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount();"; title = "count levels" }
    Check (-not $read.IsError -and [int]$read.Json.result -gt 0 -and $read.Json.log -match "20\d\d") "read-only code returns its value and log ($($read.Text))"
    $bad = Invoke-Tool "run_code" @{ code = "int a = 1;`nint b = `"two`";`nreturn a;" }
    Check ($bad.IsError -and $bad.Text -match "line 2,") "a compile error comes back on the code's own line ($($bad.Text -split "`n" | Select-Object -Last 1))"
    $throws = Invoke-Tool "run_code" @{ code = "throw new InvalidOperationException(`"on purpose`");" }
    Check ($throws.IsError -and $throws.Text -match "on purpose") "an exception comes back as its message"

    $name = { (Invoke-Tool "run_code" @{ code = "return doc.ProjectInformation.Name;" }).Json.result }
    $was = & $name
    $before = @(& $steps).Count
    $change = Invoke-Tool "run_code" @{ title = "rename project"; code = "using (var t = new Transaction(doc, `"rename`")) { t.Start(); doc.ProjectInformation.Name = `"KhMcp verify`"; t.Commit(); }`nreturn doc.ProjectInformation.Name;" }
    $after = @(& $steps)
    Check (-not $change.IsError -and $change.Json.result -eq "KhMcp verify") "code that changes the model runs"
    Check ($after.Count -eq $before + 1 -and $after[0] -eq "AI: Code - rename project") "…as one AI step ($($after[0]))"
    Invoke-Tool "undo" | Out-Null
    Start-Sleep 3
    Check ((& $name) -eq $was) "undo takes the change back"
    $log = Join-Path $env:LOCALAPPDATA ("KH-Tool\ai-code\" + (Get-Date -Format yyyyMMdd) + ".log")
    Check ((Test-Path $log) -and (Get-Content $log -Raw) -match "rename project") "every run is logged ($log)"
}

Write-Host ""
if ($failures.Count -eq 0) { Write-Host "Verify-KhMcp: PASS" -ForegroundColor Green; exit 0 }
Write-Host "Verify-KhMcp: $($failures.Count) FAILED" -ForegroundColor Red
exit 1
