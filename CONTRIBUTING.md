# Contributing

Thanks for helping. A few rules keep this add-in safe to run against real projects.

## Before you open a pull request

1. **Build every year you touched.** Revit 2024 is net48, 2025/2026 are net8 and 2027 is net10. Code that
   compiles for one can fail on another.
   ```pwsh
   dotnet build src/KhBatchRunner/KhBatchRunner.csproj -p:RevitVersion=2024 -p:RevitApiFromNuGet=true
   dotnet build src/KhBatchRunner/KhBatchRunner.csproj -p:RevitVersion=2026 -p:RevitApiFromNuGet=true
   dotnet build src/KhBatchRunner/KhBatchRunner.csproj -p:RevitVersion=2027 -p:RevitApiFromNuGet=true
   dotnet build src/KhBatchRunner/KhBatchRunner.csproj -p:RevitVersion=2026 -p:Edition=DRW -p:RevitApiFromNuGet=true
   dotnet build src/KhBatchController/KhBatchController.csproj
   ```
2. **Run the gates** listed in the README. A new window must also be added to the list in
   `tools/ThemePreview` (`Windows()`). A window missing from that list is not checked at all.
3. **Test in Revit itself.** A headless pass doesn't prove the interactive path works. Say in the
   PR which Revit year you tested, and whether the model was local or in the cloud.

## Rules that bite

- **An action's name is its schedule key.** Renaming an action breaks every saved schedule unless
  the old name is mapped in `ActionNames.Canonical`.
- **Never force-close Revit.** Use `WM_CLOSE` or `CloseMainWindow` only.
  `Verify-NoForcedRevitClose.ps1` enforces this.
- **Installed PowerShell must stay Constrained-Language-safe.** Locked-down PCs run it that way.
- **Watch the encoding.** Source files are UTF-8, and many contain an em dash (—). Windows
  PowerShell 5.1 `Get-Content`/`Set-Content` corrupts them, so edit with an editor or explicit UTF-8.
- **Keep model and client data out.** No real project, client, site or hub names go in code,
  tests, comments or docs. `install\Verify-NoClientNames.ps1` fails the build on known ones. Use
  neutral names such as `SITE1`, `BLDGA`, `Test Model`.
- **Autodesk internals.** Reflection on undocumented Autodesk code stays in its existing wrapper
  files (see SECURITY.md). Never commit Autodesk binaries or decompiled third-party code.

## A new batch feature

Every feature that runs over models behaves the same way, so someone who has used one dialog can
use the next. A new one must:

1. **Pick model sets through `ModelSetAccess`.** `BindSetCombo` for one set, a
   `DimmableCheckedListBox` with `AttachGreying` for several. That gives the access greying,
   ordering and labels for free.
2. **Save its options as a named set.** A `NamedJsonSetStore` facade, described in
   `SettingsCatalog`, with the row wired through `InputSetActions`: save prompts before it
   overwrites, a new name renames, and there is Duplicate, Delete (with a confirm) and a last-used
   restore when the dialog opens.
3. **Offer "Run on active model"** through `ActiveModelRunner`, unless the action cannot work on an
   open document. If it cannot, say why in the command's class comment.
4. **Run on a set through `BatchCommandFlow.RunModal`.** Never from an Idling event.
5. **Work in Batch Run and Schedule Run** by carrying its options as a spec on `ScheduledJob`
   (cloned in `Duplicate()`), or state in the class comment that it is dialog-only.
6. **Split through `InteractiveSplit.TryLaunch`**, or declare "no split" with the reason. *(This
   rule is under review: the parallel paths are due a separate pass.)*
7. **Show the same run options:** "When finished", a destination folder that starts on
   `UserSettings.DefaultDestinationFolder`, a project log name, and buttons named "Run" and
   "Cancel".
8. **Declare itself:** workset posture (the compiler insists), a row in the TEST.md matrix, the
   window in `ThemePreview.Windows()`, and a line in the handbook's feature table.

`install\Verify-BatchFeatureContract.ps1` checks the parts of rules 1, 2 and 7 that can be read
from the source. An exemption is a line in that script with its reason. Rules 3, 5 and 6 need
judgement: answer them in the pull request.

## Naming

- Code, assembly and on-disk names use **Kh** (`KhBatchRunner`, `KHBATCH_*`).
- User-visible text names the platform descriptively: "Autodesk Forma" the first time on a
  screen, "Forma" after that. Forma never appears in the product's own name.

## Licence

By contributing you agree that your contribution is licensed under the repository's MIT licence.
