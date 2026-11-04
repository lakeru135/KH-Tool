<#
.SYNOPSIS
  Fails packaging if a shipped PowerShell script uses constructs that a managed PC refuses.

.DESCRIPTION
  The PCs this product is diagnosed on are managed, and a managed PC runs PowerShell in
  ConstrainedLanguage mode. There, PowerShell refuses:

    * .NET method and static-member calls on anything but a few core types
    * New-Object on anything but those types
    * [pscustomobject] casts          ("ConversionSupportedOnlyToCoreTypes")
    * Add-Type                        ("CannotDefineNewType")
    * Compress-Archive                - itself a PowerShell module, so it hits the same wall

  Version 1.3.5's collector used all of these. It died at PARAMETER BINDING on
  [Environment]::GetFolderPath, printing one error and producing no bundle - so the tool that
  exists to explain a locked-down PC was disabled by the very lockdown it was sent to investigate,
  and there was nothing to send back.

  This gate exists because that failure is INVISIBLE HERE. Every one of those constructs works
  perfectly on an unmanaged developer machine; the script passes every local test and fails on the
  one PC that matters. A lint is the only thing that catches it before shipping.

  Only scripts that are INSTALLED on a user's PC are checked. Build-time scripts (this one, the
  installer builders) run on a developer machine in FullLanguage and are not restricted.
#>
param()

$ErrorActionPreference = "Stop"

# Shipped by the installers into ...\Kanghun BIM\<product>\Support and the per-year Addins folders.
$shipped = @(
    "Collect-ScheduleDiagnostics.ps1",
    "Loop-TestScheduleLaunch.ps1"
)

# Types whose members ARE callable in ConstrainedLanguage. Verified by probe on Windows 11 /
# PowerShell 5.1, not taken from documentation: string, regex and datetime statics all work.
$allowedTypes = @(
    'string', 'int', 'int32', 'int64', 'long', 'bool', 'boolean', 'char', 'byte',
    'double', 'single', 'float', 'decimal', 'datetime', 'timespan', 'guid', 'version',
    'regex', 'hashtable', 'array', 'math', 'void', 'switch', 'ordered', 'type'
)

$violations = @()

foreach ($name in $shipped) {
    $path = Join-Path $PSScriptRoot $name
    if (-not (Test-Path -LiteralPath $path)) { continue }

    $lineNumber = 0
    $inBlockComment = $false
    $exempt = $false
    foreach ($line in (Get-Content -LiteralPath $path)) {
        $lineNumber++

        # A block comment describing this very rule must not trip it.
        if ($line -match '<#') { $inBlockComment = $true }
        if ($inBlockComment) {
            if ($line -match '#>') { $inBlockComment = $false }
            continue
        }

        # Escape hatch for code that is REACHED ONLY when the language mode is already known to be
        # FullLanguage - the notification probe deliberately runs the blocked API to see what it
        # says. Region-scoped and it must state a reason, so it cannot be sprinkled on a whim.
        if ($line -match '#\s*clm-safe-ignore-begin\s*:\s*\S') { $exempt = $true; continue }
        if ($line -match '#\s*clm-safe-ignore-end')            { $exempt = $false; continue }
        if ($exempt) { continue }

        # Line comments describe the rule; they must not trip it either.
        $code = $line -replace '(?<!`)#.*$', ''
        if ($code.Trim() -eq '') { continue }

        $problems = @()

        if ($code -match '\bNew-Object\b')        { $problems += 'New-Object' }
        if ($code -match '\[pscustomobject\]')    { $problems += '[pscustomobject]' }
        if ($code -match '\bAdd-Type\b')          { $problems += 'Add-Type' }
        if ($code -match '\bCompress-Archive\b')  { $problems += 'Compress-Archive' }

        # [Some.Type]::Member - allowed only for the core types above.
        foreach ($m in ([regex]::Matches($code, '\[([A-Za-z0-9_.]+)\]::'))) {
            $typeName = $m.Groups[1].Value
            $leaf = ($typeName -split '\.')[-1]
            if ($allowedTypes -notcontains $typeName.ToLower() -and
                $allowedTypes -notcontains $leaf.ToLower()) {
                $problems += "[$typeName]::"
            }
        }

        foreach ($p in $problems) {
            $violations += [pscustomobject]@{
                File = $name
                Line = $lineNumber
                What = $p
                Text = $code.Trim()
            }
        }
    }
}

if ($violations) {
    $detail = $violations | ForEach-Object { "$($_.File):$($_.Line): $($_.What)  --  $($_.Text)" }
    throw "Shipped PowerShell must run under ConstrainedLanguage; these will fail on a managed PC:" +
          [Environment]::NewLine + ($detail -join [Environment]::NewLine) +
          [Environment]::NewLine +
          "Use cmdlets and core types instead (Get-ItemPropertyValue, Get-Date -Format, arrays and " +
          "hashtables, tar.exe for zipping)."
}

Write-Host "ConstrainedLanguage gate passed: shipped scripts use no blocked constructs." -ForegroundColor Green
