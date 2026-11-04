<#
.SYNOPSIS
  Fails the build if any shared dialog throws while being constructed, is unreadable, loses a
  schedule field, or shows model sets without the signed-in-account warning - in words OR in ink.

.DESCRIPTION
  A WinForms window that throws in its own constructor is invisible to the compiler and to every
  offline test that does not build one. C# definite-assignment does not apply to instance fields,
  so reading a field before assigning it compiles clean and throws NullReferenceException at run
  time - inside the constructor, before anything can catch it.

  THIS GATE EXISTS BECAUSE THAT SHIPPED. Version 1.3.8.2's ScheduleRunWindow dereferenced
  _exportNwcSetCombo two lines before constructing it. Every caller died: the Schedule Run button,
  Batch Run's "Save as schedule", and the controller's Schedule Run tile - the user saw Revit's
  "Object reference not set to an instance of an object" and could not open the window at all. The
  build was clean, 398 harness cases passed, and both other gates passed.

  tools\ThemePreview already constructed that exact window (it renders screenshots of every
  dialog), with the SetDllDirectory + AssemblyResolve shim that makes Revit-typed windows loadable
  outside Revit. It caught nothing for two reasons, both now fixed: it swallowed the exception and
  exited 0, and nothing ever ran it. This script is the "ran it" half.

  Run it beside Verify-NoForcedRevitClose.ps1 and Verify-ConstrainedLanguageSafe.ps1 when cutting a
  build. It needs a real Revit install for the API assemblies (ThemePreview resolves them from
  C:\Program Files\Autodesk\Revit 2026) and a desktop session, but it opens nothing on screen -
  "check" mode builds each window and disposes it without rendering.
#>
param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo "tools\ThemePreview\ThemePreview.csproj"

if (-not (Test-Path $project)) {
    Write-Error "ThemePreview project not found at $project"
    exit 2
}

Write-Host "Constructing every shared dialog..."
& dotnet run --project $project -c $Configuration -- check
$code = $LASTEXITCODE

if ($code -ne 0) {
    Write-Host ""
    Write-Host "Window construction gate FAILED - a dialog threw while being built." -ForegroundColor Red
    Write-Host "The exception and the window are named above." -ForegroundColor Red
    exit 1
}

# Second check, same harness: FAULT-INJECT a real Win32 message box and prove ForeignDialogBreaker
# finds it, quotes it and clicks it. On 2026-08-15 the Navisworks exporter's "No suitable geometry
# found" box stopped four parallel Revits for 8.5 minutes, and only a person at the keyboard
# restarted them - so a watchdog for it that has never been seen to fire is not a watchdog. It has
# already earned its keep: the first implementation posted WM_COMMAND to the dialog, which a
# MessageBox ignores, and this check is what caught it.
Write-Host "Fault-injecting a modal dialog..."
& dotnet run --project $project -c $Configuration -- dialog
$dialogCode = $LASTEXITCODE

if ($dialogCode -ne 0) {
    Write-Host ""
    Write-Host "Dialog breaker gate FAILED - a modal dialog was not answered." -ForegroundColor Red
    Write-Host "An unattended run would sit behind it indefinitely." -ForegroundColor Red
    exit 1
}

# Third check: is every dialog READABLE? Constructing a window says nothing about whether its
# controls fit. On 2026-08-15 two shipped unreadable - Export NWC's "Model set to run over:" label
# was sliced in half under the row below it with its list crushed to 4px, and Schedule Run's ad-hoc
# and actions columns were one row high - and both had passed construction, 465 harness cases and
# every other gate here. This shows each window off-screen at its natural size AND at its minimum
# size (where the faults surface first) and fails on truncated text, overlapping siblings, a control
# outside its parent, or a docked list starved to nothing. --inject plants a deliberately broken
# form and fails if the checker does not flag it, the same discipline as the dialog breaker above.
Write-Host "Checking every window is readable..."
& dotnet run --project $project -c $Configuration -- layout --inject
$layoutCode = $LASTEXITCODE

if ($layoutCode -ne 0) {
    Write-Host ""
    Write-Host "Layout gate FAILED - a dialog has controls that are clipped, overlapping or starved." -ForegroundColor Red
    Write-Host "Each line above names the window, the size it was checked at, and the control." -ForegroundColor Red
    exit 1
}

# --- 4. ROUND TRIP -----------------------------------------------------------------------------
# The three checks above prove a window BUILDS and is READABLE. None of them proves it still SAVES
# THE RIGHT SCHEDULE. 1.4.0 collapses four near-identical schedule windows onto shared panels, and
# the whole risk of that is a field which quietly stops being wired - a fault no screenshot can
# show, whose first symptom is a schedule firing at the wrong time next month.
#
# This loads a job into each schedule window, reads it back, and compares. Twice: once against what
# went in (catching a field the refactor forgot), and once against its own output (catching drift in
# anything the owned-property list does not name). --inject requires the comparison to notice a
# deliberately wrong value before its pass is believed.
#
# Recorded against 1.3.9's untouched windows BEFORE the refactor began, which is the only ordering
# that means anything: a gate written afterwards proves the new code agrees with itself.
Write-Host "Checking every schedule window still saves what it loaded..."
& dotnet run --project $project -c $Configuration -- roundtrip --inject
$roundTripCode = $LASTEXITCODE

if ($roundTripCode -ne 0) {
    Write-Host ""
    Write-Host "Round-trip gate FAILED - a schedule window lost a field between load and save." -ForegroundColor Red
    Write-Host "Each FAIL line names the window, the property, and both values." -ForegroundColor Red
    exit 1
}

# --- 5. ACCOUNT GREYING: CAN THE COMPARISON ITSELF FAIL? -----------------------------------------
# This one now guards the COMPARATOR, on an isolated synthetic combo; step 6 guards the real
# controls. It still earns its place: it is the only check that puts a deliberately no-op dimming
# rule on an otherwise perfectly wired control and requires the measurement to notice. 1.5.1 shipped
# dimming that did nothing on exactly that shape - the theme owner-draws every combo, so its painter
# repainted every row flat on top - and no screenshot could show it, because a ComboBox popup is a
# separate window Form.DrawToBitmap never captures. Run twice: "dim --inject" only proves the
# comparison can fail, and the plain run is the one that checks the real thing.
Write-Host "Checking an inaccessible model set draws dimmed..."
& dotnet run --project $project -c $Configuration -- dim
$dimCode = $LASTEXITCODE
if ($dimCode -eq 0) {
    & dotnet run --project $project -c $Configuration -- dim --inject
    $dimCode = $LASTEXITCODE
}

if ($dimCode -ne 0) {
    Write-Host ""
    Write-Host "Dimming gate FAILED - the account greying is not reaching the pixels." -ForegroundColor Red
    exit 1
}

# --- 6. ACCOUNT GREYING: DOES EVERY WINDOW ACTUALLY PAINT IT? -----------------------------------
# THREE assertions per model-set list, in BOTH themes: it went through ModelSetAccess.AttachGreying,
# its inaccessible row carries the "no access" suffix, and - the one that matters - the row is drawn
# in measurably dimmer INK than an accessible one.
#
# The third exists because the first two both passed while three controls painted nothing.
# CheckedListBox overrides DrawMode with an EMPTY setter and never raises DrawItem, so the
# owner-draw handler registered against Batch Run, the Multi Model action picker and the shared
# schedule panel had never once run. Registration proves a rule was wired; a label proves a string
# was built; only ink proves a pixel changed. The user saw the greying in the dropdowns and nowhere
# else, and every gate was green.
#
# --inject plants THE BUG ITSELF - a raw CheckedListBox handed to AttachGreying, correctly
# registered and correctly labelled - and fails if that is not caught. Same discipline as the layout
# gate, and like it, the real windows are still checked in the same run.
#
# It constructs AND shows every window twice (once per theme), so expect it to take appreciably
# longer than the other gates. That is not a hang.
Write-Host "Checking every window that shows model sets paints the unopenable ones dimmer..."
& dotnet run --project $project -c $Configuration -- coverage --inject
$coverageCode = $LASTEXITCODE

if ($coverageCode -ne 0) {
    Write-Host ""
    Write-Host "Coverage gate FAILED - a window shows model sets without the account treatment." -ForegroundColor Red
    Write-Host "Each line above names the window and the control." -ForegroundColor Red
    exit 1
}

# --- 7. SETTINGS CATALOG ------------------------------------------------------------------------
# Import/export is only as complete as SettingsCatalog. A store added later keeps working perfectly
# and is silently absent from every exported bundle, which nobody finds out until a restored PC is
# missing one tool's saved sets. Reflection over the shipped assembly finds the stores; this asserts
# the catalogue has kept up with them. Two runs, for the same reason as the dimming gate.
Write-Host "Checking every settings store is in the export catalogue..."
& dotnet run --project $project -c $Configuration -- catalog
$catalogCode = $LASTEXITCODE
if ($catalogCode -eq 0) {
    & dotnet run --project $project -c $Configuration -- catalog --inject
    $catalogCode = $LASTEXITCODE
}

if ($catalogCode -ne 0) {
    Write-Host ""
    Write-Host "Settings-catalog gate FAILED - a store would be missing from exported bundles." -ForegroundColor Red
    exit 1
}

# --- 8. THE SAME INK, ON THE FRAMEWORK STEP 6 CANNOT REACH --------------------------------------
# ThemePreview is net8-only (it uses System.Windows.Forms.TaskDialog, which does not exist on .NET
# Framework), so everything above proves the dimming for Revit 2025/2026 and says NOTHING about
# Revit 2024, which ships the same source compiled against net48 - where CheckedListBox is a
# different implementation. The whole mechanism rests on a framework behaviour: that
# CheckedListBox.OnDrawItem honours the ForeColor it is handed, because its own DrawMode setter is
# empty and it never raises DrawItem. A fact about one runtime is not a fact about the other.
#
# tools\DimInk compiles the REAL DimmableCheckedListBox for both targets - the product files are
# LINKED, not copied - and measures the ink. It also asserts the premise: setting DrawMode must read
# back Normal. Plain then inject, per target, for the same reason as step 5.
Write-Host "Checking the dimming paints on net48 (Revit 2024) as well as net8..."
$dimInk = Join-Path $repo "tools\DimInk\DimInk.csproj"
$inkCode = 0
foreach ($tfm in @("net48", "net8.0-windows")) {
    & dotnet run --project $dimInk -c $Configuration -f $tfm
    if ($LASTEXITCODE -ne 0) { $inkCode = $LASTEXITCODE; break }
    & dotnet run --project $dimInk -c $Configuration -f $tfm -- --inject
    if ($LASTEXITCODE -ne 0) { $inkCode = $LASTEXITCODE; break }
}

if ($inkCode -ne 0) {
    Write-Host ""
    Write-Host "DimInk gate FAILED - the account dimming does not paint on one of the two target frameworks." -ForegroundColor Red
    exit 1
}

Write-Host "Window construction gate passed."
exit 0
