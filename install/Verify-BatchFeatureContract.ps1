<#
.SYNOPSIS
  Fails when a dialog breaks the shared behaviour every batch feature is meant to have.

.DESCRIPTION
  CONTRIBUTING.md ("A new batch feature") lists what a feature that runs over models must do. Three
  of those rules can be read straight off the source, so they are checked here; the rest need
  judgement and stay on the pull-request checklist.

    1. A "Save set" button is wired through InputSetActions, so saving prompts before overwriting,
       a new name renames, and Duplicate / Delete behave the same in every dialog.
    2. A dialog that lists model sets goes through ModelSetAccess, so a set this account cannot
       open is ordered, labelled and dimmed the same everywhere.
    3. A folder box is not seeded from the Desktop; it starts on the last folder a run used
       (Settings.UserSettings.DefaultDestinationFolder).

  Added after an audit found each rule followed by some dialogs and not others: Export NWC and
  Export IFC + Data overwrote and deleted a saved set without asking while five sibling dialogs
  asked, and ten dialogs started on the Desktop while four started on the last-used folder. None of
  that fails a build or a layout check, so nothing noticed.

  An exemption is a line in $exempt below, with its reason. That makes leaving the rule a visible
  decision in a diff rather than something a new dialog does by not knowing the rule exists.

.PARAMETER Inject
  Also checks three planted sources, one per rule, and fails unless each is reported - proof that
  the gate can still fail. The real sources are checked in the same run.
#>
param([switch]$Inject)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$uiDir = Join-Path $root "src\KhBatchRunner\UI"

# File name -> rule -> why it is allowed to differ.
$exempt = @{
    "FormaBrowserWindow.cs"      = @{ SaveSet = "The model-set editor: the set is the document being edited, and it has its own New / rename-on-save / Duplicate / Delete with confirms." }
    "EditScriptSetWindow.cs"     = @{ SaveSet = "The script-set editor, same shape as the model-set editor." }
    "CheckSheetPublishWindow.cs" = @{ SaveSet = "Saves a Forma FOLDER set (FolderSetStore), shared with Get File Log; it confirms overwrite and delete itself." }
}

function Test-Source {
    param([string]$Name, [string]$Text)

    $found = @()
    $allow = $exempt[$Name]

    if ($Text -match '"Save set"' -and $Text -notmatch 'InputSetActions<' -and
        $Name -ne "InputSetActions.cs" -and -not ($allow -and $allow.SaveSet)) {
        $found += "${Name}: has a ""Save set"" button but does not use InputSetActions (rule 1)."
    }

    $listsSets = $Text -match '\bModelPreset\b' -and
                 $Text -match 'new\s+(ComboBox|ListBox|CheckedListBox|DimmableCheckedListBox)\b'
    if ($Name -like "*Window.cs" -and $listsSets -and $Text -notmatch 'ModelSetAccess\.' -and
        -not ($allow -and $allow.ModelSets)) {
        $found += "${Name}: lists model sets but does not go through ModelSetAccess (rule 2)."
    }

    if ($Text -match 'new\s+TextBox\s*\{\s*Text\s*=\s*(System\.)?Environment\.GetFolderPath' -and
        -not ($allow -and $allow.Folder)) {
        $found += "${Name}: seeds a folder box from a special folder; use Settings.UserSettings.DefaultDestinationFolder (rule 3)."
    }

    return $found
}

$violations = @()
foreach ($file in Get-ChildItem $uiDir -Recurse -File -Filter *.cs) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    $violations += Test-Source -Name $file.Name -Text $text
}

# An exemption for a file that no longer exists, or no longer needs it, is dead weight that would
# silently cover the next file given that name.
foreach ($name in $exempt.Keys) {
    $path = Get-ChildItem $uiDir -Recurse -File -Filter $name | Select-Object -First 1
    if (-not $path) { $violations += "${name}: listed as exempt but the file does not exist." }
}

if ($Inject) {
    $planted = @(
        @{ Name = "InjectSaveWindow.cs";   Rule = "rule 1"; Text = 'var b = new Button { Text = "Save set" };' }
        @{ Name = "InjectSetsWindow.cs";   Rule = "rule 2"; Text = 'ModelPreset p; var c = new ComboBox { };' }
        @{ Name = "InjectFolderWindow.cs"; Rule = "rule 3"; Text = '_folderBox = new TextBox { Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) };' }
    )
    foreach ($p in $planted) {
        $hits = @(Test-Source -Name $p.Name -Text $p.Text)
        if ($hits.Count -ne 1 -or $hits[0] -notmatch [regex]::Escape($p.Rule)) {
            throw "Batch-feature contract gate is blind: the planted $($p.Rule) violation was not reported."
        }
    }
    Write-Host "Inject: all three planted violations were reported." -ForegroundColor DarkGray
}

if ($violations) {
    throw "Batch-feature contract broken (see CONTRIBUTING.md, 'A new batch feature'):" +
          [Environment]::NewLine + ($violations -join [Environment]::NewLine)
}

Write-Host "Batch-feature contract gate passed: save-set rows, model-set lists and folder defaults are shared." -ForegroundColor Green
