<#
.SYNOPSIS
  Proves that a new batch action cannot skip the workset question.

.DESCRIPTION
  Every IBatchAction must declare WorksetPosture Worksets and a WorksetPostureReason. Both are
  ABSTRACT on BatchActionBase, so the C# compiler is the gate: an action that does not answer does
  not build.

  This script exists because a gate nobody has watched fail is not known to work. It:

    1. FAULT INJECTION - writes an action that answers everything EXCEPT the posture, builds, and
       requires the build to FAIL with CS0534. If that build succeeds, the gate is gone (someone
       gave the members a default, or the probe stopped deriving from BatchActionBase) and this
       script fails loudly. Verify-WindowConstruction.ps1 earned its keep the same way.
    2. NO SIDE DOOR - OpensAllWorksets and PlansWorksetOpen are pure derivations of Worksets and
       non-virtual, so overriding them is already CS0506. A `new` declaration would compile and
       silently diverge from the declared posture, so any re-declaration outside BatchActionBase
       and IBatchAction fails here.
    3. CLEAN REBUILD - removes the probe and rebuilds, so the tree is left buildable either way.

  WHY THIS GATE EXISTS AT ALL. BatchEngine opens every model with CloseAllWorksets because it is
  much cheaper on cloud models, and an element in a closed workset is not loaded - a collector never
  returns it. For an action that reads element INSTANCES that is not a cheap open, it is a wrong
  answer with no error. It cost five builds of Export NWC writing empty .nwc files (1.3.8.4-1.3.8.8)
  and four versions of Batch Interop calling partial models clean. Both got through because
  BatchActionBase answered the question on the author's behalf, with a default, and the default was
  wrong.

.NOTES
  Like the window gate, this runs `dotnet build` WITHOUT -p:Version, so it leaves the DLL stamped
  1.0.0.0. Build the installers AFTER the gates, never before.
#>
param(
    [string]$Configuration = "Release",
    [string]$RevitVersion = "2026"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\KhBatchRunner\KhBatchRunner.csproj"
$probe = Join-Path $root "src\KhBatchRunner\Actions\_PostureGateProbe.cs"

# --- 2. No side door ---------------------------------------------------------------------------
# Checked before the expensive builds: a cheap failure should be reported cheaply.
$owned = @("BatchActionBase.cs", "IBatchAction.cs")
$sideDoors = Get-ChildItem (Join-Path $root "src\KhBatchRunner") -Recurse -File -Filter *.cs |
    Where-Object { $owned -notcontains $_.Name } |
    Select-String -Pattern 'bool\s+(OpensAllWorksets|PlansWorksetOpen)\s*(=>|\{)'

if ($sideDoors) {
    $detail = $sideDoors | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
    throw "OpensAllWorksets/PlansWorksetOpen are derived from WorksetPosture and must not be " +
          "re-declared - set Worksets instead:" +
          [Environment]::NewLine + ($detail -join [Environment]::NewLine)
}

Write-Host "No side door: the workset flags are declared only on BatchActionBase/IBatchAction." -ForegroundColor Green

# --- 1. Fault injection ------------------------------------------------------------------------
# Answers Name, Columns and Run so that the ONLY thing missing is the posture. If the build fails
# for some other reason the CS0534 assertion below will not find its evidence, and the gate says so
# rather than passing on a coincidence.
$probeSource = @'
// TEMPORARY - written and deleted by install\Verify-ActionPosture.ps1. If this file is present in
// a commit, the gate was interrupted; delete it.
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Actions;

internal sealed class PostureGateProbe : BatchActionBase
{
    public override string Name => "GATE PROBE";

    public override IReadOnlyList<string> Columns { get; } = new[] { "Result" };

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc) =>
        new Dictionary<string, string> { ["Result"] = "never runs" };
}
'@

try {
    Set-Content -LiteralPath $probe -Value $probeSource -Encoding utf8

    Write-Host "Injecting an action with no declared workset posture..." -ForegroundColor Cyan
    $output = & dotnet build $project -c $Configuration -p:RevitVersion=$RevitVersion --nologo -v q
    $buildSucceeded = ($LASTEXITCODE -eq 0)
    $text = ($output | Out-String)

    if ($buildSucceeded) {
        throw "THE GATE IS GONE. An action declaring no WorksetPosture compiled. Worksets and " +
              "WorksetPostureReason must be abstract on BatchActionBase - a default lets the next " +
              "action read a partial model and report success, which is the failure this gate exists " +
              "to make impossible."
    }

    if ($text -notmatch 'CS0534' -or $text -notmatch 'PostureGateProbe') {
        throw "The injected build failed, but not for the expected reason (no CS0534 naming " +
              "PostureGateProbe). The gate cannot be said to have been proven. Build output:" +
              [Environment]::NewLine + $text
    }

    Write-Host "Gate proven: the probe failed with CS0534 as required." -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe -Force }
}

# --- 3. Clean rebuild --------------------------------------------------------------------------
Write-Host "Rebuilding without the probe..." -ForegroundColor Cyan
& dotnet build $project -c $Configuration -p:RevitVersion=$RevitVersion --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "The tree does not build with the probe removed. Check that " +
          "src\KhBatchRunner\Actions\_PostureGateProbe.cs is gone."
}

Write-Host "Action-posture gate passed." -ForegroundColor Green
