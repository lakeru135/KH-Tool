# KH-Tool — Build, Install & Test Guide

Three layers of testing, cheapest first:

1. **Offline gates** — no Revit needed; run before every commit that touches shared code.
2. **Live feature matrix** — every feature against real models, driven headlessly; run before a
   release and around any refactor (once on the old build, once on the new one, then diff).
3. **Human click-through** — about 15 minutes; the part automation cannot honestly cover.

The ribbon tab is **KH-Tool** (Full edition) or **DRW Helper** (DRW edition).

## Build
Requires the .NET 8 SDK. Pass `-p:RevitApiFromNuGet=true` when that Revit year is not installed,
**and for Revit 2026 whenever the installed Revit is 2026.5 or later** (its own API assemblies need
a newer runtime than the .NET 8 SDK compiles against, and the build fails with `CS1705`).

```pwsh
dotnet build src\KhBatchRunner\KhBatchRunner.csproj -c Release -p:RevitVersion=2026 -p:RevitApiFromNuGet=true
dotnet build src\KhBatchRunner\KhBatchRunner.csproj -c Release -p:RevitVersion=2025 -p:RevitApiFromNuGet=true
dotnet build src\KhBatchRunner\KhBatchRunner.csproj -c Release -p:RevitVersion=2024 -p:RevitApiFromNuGet=true
dotnet build src\KhBatchRunner\KhBatchRunner.csproj -c Release -p:RevitVersion=2026 -p:Edition=DRW -p:RevitApiFromNuGet=true
dotnet build src\KhBatchRunner\KhBatchRunner.csproj -c Release -p:RevitVersion=2026 -p:Edition=FullUnlimited -p:RevitApiFromNuGet=true
dotnet build src\KhBatchController\KhBatchController.csproj
dotnet build src\KhBatchLauncher\KhBatchLauncher.csproj
dotnet build src\KhMcp\KhMcp.csproj
```

A change is only "built" when the whole matrix is green: 2024 (net48), 2025, 2026; 2026 × DRW and
FullUnlimited; Debug and Release for 2026 Full; Controller, Launcher, KhMcp and the `tools\*`
projects.

## Dev install
```pwsh
# Close Revit first (the dll gets locked). Never force-close it.
.\install\Install-Dev.ps1 -RevitVersion 2026 -Configuration Release
.\install\Install-Dev.ps1 -RevitVersion 2026 -Configuration Release -Edition DRW
# Put the machine-wide installed build back in charge:
.\install\Install-Dev.ps1 -RevitVersion 2026 -RestoreInstalled
```
Copies the build into `%AppData%\Autodesk\Revit\Addins\<year>\` (DRW Helper into its own
`DrwHelper\` subfolder). If the script stops on "KhMcp.exe is being used by another process", an
AI client is running the kh-tool MCP proxy: end that session first. `KhMcp.exe` is not loaded by
Revit, so the add-in itself is unaffected.

## 1. Offline gates (no Revit UI needed)
Run all of them; none may be edited to pass.

```pwsh
.\install\Verify-NoClientNames.ps1
.\install\Verify-NoForcedRevitClose.ps1
.\install\Verify-ConstrainedLanguageSafe.ps1
.\install\Verify-WindowConstruction.ps1 -Configuration Release
.\install\Verify-ActionPosture.ps1
.\install\Verify-BatchFeatureContract.ps1 -Inject
.\install\Verify-KhMcp.ps1
dotnet run --project tools\LivenessSim
dotnet run --project tools\BundleTest
```
(Set `$env:RevitApiFromNuGet='true'` first on a PC where the 2026 build needs it.)

What the window and action gates prove, and why each exists:

- **Every window is READABLE** — `install\Verify-WindowConstruction.ps1 -Configuration Release`.
  Beyond constructing each dialog and fault-injecting a modal message box, it now runs
  `tools\ThemePreview -- layout --inject`, which shows all 14 shared dialogs off-screen at their
  natural size **and at their minimum size** and fails the build on any of:
  text wider (or, when it wraps, taller) than the control showing it; two sibling controls
  overlapping; a control outside its parent; a docked list or grid starved below 48px.
  `--inject` plants a deliberately broken form and fails if the checker does not catch it.
  *Added 1.3.8.9, after two dialogs shipped unreadable in 1.3.8.8 — a clipped label sitting on top
  of the row below it, and two list columns squeezed to one row — having passed every other check.
  Constructing a window says nothing about whether you can read it.*
- **Every schedule window still SAVES what it loaded** —
  `tools\ThemePreview -- roundtrip --inject`, run by `install\Verify-WindowConstruction.ps1`.
  Loads a job into each of the four schedule windows, reads the form back, and compares. Twice:
  once against what went in over the properties that window OWNS (catching a field the refactor
  forgot to wire), and once against its own output, which must be a fixed point (catching drift in
  properties the owned list does not name). Both are needed — deleting `ProjectLogName` from
  `ScheduleCutWindow`'s mapping leaves the fixed-point check silent, because both trips lose it
  identically. Fixtures are your own saved schedules where they still load, with a synthetic
  ad-hoc-model job as the floor; a window with neither is reported SKIPPED, and the gate FAILS if
  it checked nothing at all.
  *Added 1.4.0, before the refactor that needed it, and recorded against the untouched 1.3.9
  windows first — a gate written after the change only proves the new code agrees with itself. The
  layout gate proves a window draws; nothing proved it still saves the right schedule, and that
  fault's first symptom would be a schedule firing at the wrong time next month.*
- **Every window that shows model sets PAINTS the ones this account cannot open dimmer** —
  `tools\ThemePreview -- coverage --inject`, run by `install\Verify-WindowConstruction.ps1`.
  Constructs and shows every window in `ThemePreview.Windows()` against a seeded account snapshot
  and two seeded sets (one reachable, one not), walks the real control tree, and makes **three**
  assertions per model-set list, **in both themes**: it went through `ModelSetAccess.AttachGreying`,
  its inaccessible row carries the suffix, and that row is drawn in measurably dimmer **ink** than
  an accessible one — raised through the control's own `OnDrawItem`. `--inject` plants *the bug
  itself*, a raw `CheckedListBox` handed to `AttachGreying` which passes the first two checks and
  fails only the third, and like the layout gate it still checks the real windows in the same run.
  *The ink assertion was added after the first two both passed while three controls painted
  nothing: `CheckedListBox` overrides `DrawMode` with an empty setter and never raises `DrawItem`,
  so the handler registered against Batch Run, the Multi Model action picker and the shared schedule
  panel had never once run. Registration proves a rule was wired and a label proves a string was
  built; only ink proves a pixel changed.*
- **And it paints on the framework that gate cannot reach** — `tools\DimInk`, run for `net48` and
  `net8.0-windows`, plain then `--inject`, by `install\Verify-WindowConstruction.ps1`. ThemePreview
  is net8-only, so it says nothing about Revit 2024. DimInk compiles the real
  `DimmableCheckedListBox` for both targets — the product files are **linked, not copied** — and
  measures the same ink, and asserts the premise the whole mechanism rests on: that setting
  `DrawMode = OwnerDrawFixed` reads back `Normal`.
  *Added 1.5.1.2, because the rule reached eight of the fifteen windows that let you pick a model
  set. It lived in one class and was applied window by window by hand; Batch Interop, Batch Link
  RVT, Correct Workset, Cut model,
  Initiate to Forma, Internal Relink and Publish Cloud Model each showed an unopenable set exactly
  like any other, and the user reported seeing the flag in Edit Model Set and nowhere else. The
  class comment naming which windows were covered was itself seven windows out of date. Neither
  grep nor a screenshot can see this — a window builds its combo in a constructor and fills it
  eighty lines away, and a ComboBox popup is a separate window `DrawToBitmap` never captures.*
- **The greying actually reaches the PIXELS** — `tools\ThemePreview -- dim`, then `dim --inject`,
  both run by `install\Verify-WindowConstruction.ps1`. Raises a combo's own `DrawItem` for a
  dimmed row and an undimmed one and compares the ink, in both themes.
  *1.5.1 shipped dimming that did nothing: the theme owner-draws every combo in dark mode, so its
  painter repainted every row flat on top of it. Sorting still worked, so the list looked
  half-right. Two runs, because `--inject` only proves the comparison can fail.*
- **Every settings store is in the EXPORT catalogue** — `tools\ThemePreview -- catalog`, then
  `catalog --inject`, both run by `install\Verify-WindowConstruction.ps1`. Reflection over the
  shipped assembly finds every folder a `*Store` writes to and requires `SettingsCatalog` to
  describe it, or to list it as deliberately not settings.
  *A store added later keeps working perfectly and is silently missing from every exported bundle;
  nobody finds out until a restored PC has lost one tool's saved sets.*
- **Every action DECLARES what a closed workset means for it** —
  `install\Verify-ActionPosture.ps1`. `WorksetPosture Worksets` and `WorksetPostureReason` are
  abstract on `BatchActionBase`, so the C# compiler is the gate; this script proves the gate is
  still live by injecting an action that does not answer and requiring the build to fail with
  `CS0534`, then rebuilding clean. It also fails if `OpensAllWorksets`/`PlansWorksetOpen` are
  re-declared outside `BatchActionBase`/`IBatchAction` — they are derivations of the posture, and a
  separate declaration is how the two would quietly diverge.
  *Added 1.3.9. The engine opens every model with all user worksets closed, and an element in a
  closed workset is not loaded — so an action that reads instances gets an empty model and reports
  success. That cost five builds of Export NWC writing empty `.nwc` files and four versions of Batch
  Interop calling partial models clean, both because a `virtual … => false` default answered the
  question for the author. An abstract member makes the compiler ask instead.*

## 2. Live feature matrix
Every feature, run against three kinds of model: **workshared cloud**, **non-workshared cloud**
(single-user cloud model, no worksets) and **local files**. Revit 2026 gets the full matrix;
Revit 2024 (net48) gets the starred rows. Revit 2025 is build-only unless it is installed.

Rules for a live run:

- Write only to a **disposable test location** you own. A model set that resolves anywhere else is
  read-only or skipped.
- The location's names and GUIDs never enter tracked files — `Verify-NoClientNames.ps1` scans the
  whole working tree. Keep fixtures, job files and outputs outside the repo or under gitignored
  `tmp\`. A workshared central's `_backup\` folder is binary that the gate reads as text, so
  workshared fixtures must live **outside** the repo.
- Never force-close Revit. A run that leaves Revit open is a failed run to inspect, not to kill.
- For a refactor: run the matrix on the **old** build first and keep every CSV/XLSX/log as the
  golden reference. A failure there is a pre-existing bug — write it down and do not fix it in
  the refactor. Then install the new build, run again, and diff after masking timestamps, run
  ids, durations and newly created model GUIDs. Every remaining difference is explained or fixed.

### Fixtures
Small models are enough, but they need content for every check to report on: phases, user
worksets, an RVT link between the models, an imported CAD and a linked CAD, a duplicate-wall
warning, sheets with revisions, rooms, a design option set, two site locations and a type-mark
text note. Build one seed, then save it as workshared and as non-workshared, locally and (through
Initiate to Forma) in the cloud. Keep a pristine copy and restore it before every write row.
Revit has no API to create design options; make that set by hand once in the seed.

### Driving it without clicking
- **Batch features** — write a `ScheduledJob` JSON (schema 3, specs snapshotted on the job, the
  shape Schedule Run saves) into `%AppData%\KhBatchRunner\Scheduled\` and start Revit with
  `KHBATCH_JOB=<job id>` and `KHBATCH_OWNER=Full`, as `install\Loop-TestScheduledRun.ps1` does.
  With "Close Revit when complete" Revit exits by itself; read the tracking CSV, the detail CSVs
  and the logs from the job's destination folder. `KHBATCH_DEV_ACTIONS=1` adds the seed action.
- **Active-model features** — start Revit with `KHMCP_AUTOSTART=1` (and `KHMCP_CODE=1` only while
  needed) and call the `kh-tool` MCP tools through `KhMcp.exe`, as `install\Verify-KhMcp.ps1`
  does. Check each change with `export_view_png` or a read-only `run_code` query.
- **Built-in self-tests** — `DRW_SELFTEST` + `DRW_SELFTEST_MODEL` (DRW edition),
  `PARAMHELPER_SELFTEST` + `PARAMHELPER_SELFTEST_DIR`, and `KHBATCH_SELFTEST_V9` with the spike
  variables.
- **Batch Link RVT** has no schedule spec (it runs only from its own dialog), so drive the code
  path behind its dialog through `run_code`.

### The matrix

| Area | Features | WS cloud | Non-WS cloud | Local |
|---|---|---|---|---|
| Model sets | Browse Forma, save set, folder set re-scan, GUID mapping check, access greying | ✔ | ✔ | ✔ |
| Multi Model Check★ | Get Site Location & PBP, Get Phase & Workset, Get Design Option, Get Link Workset, Drawing Progress, Type Mark Text Note; CSV + XLSX matrices | ✔ | ✔ | ✔ |
| Rebuild Report | Phase & Workset matrix, Design Option matrix, Batch Interop Summary, from the run's CSVs | n/a (files) | | |
| Model Batch Action★ | Purge Import CAD, Isolate Warnings (dry run, then sync/save) | ✔ sync | ✔ save | ✔ save |
| | Batch Link RVT (one workset / one per link), Correct Workset (Check, Fix) | ✔ | ✔ expect a clean "not workshared" outcome | ✔ |
| | Batch Interop (needs the Autodesk Model Checker; else record SKIPPED) | ✔ | ✔ | ✔ |
| Forma Batch Action | Initiate to Forma★, Cut Model, Internal Relink | ✔ | ✔ | source |
| | Get File Log, Get Data Extract, Check Sheet Publish (REST only) | folder set | | |
| NWD Helper | Export NWC (needs the Navisworks exporter; else SKIPPED), Export IFC + Data (+ `.frag`, `latest\`), Create rfa from NWD | ✔ | ✔ | ✔ |
| Batch / Schedule Run★ | mixed sets + ad-hoc models, action → script → action order, a Dynamo script set, auto-split into parallel Revits, supervisor recovery, each completion mode, Task Scheduler register → on-demand run → delete, toast | ✔ | ✔ | ✔ |
| Active model | Export Excel → edit → Import Excel, Formula Rules / Apply Now, Mirror Check, every MCP tool | ✔ | ✔ | ✔ |
| DRW edition★ | Enlarge Room / Stair / Door & Window, Datum Dimension, Match Datums, Single Font, Stair Annotation, Preset Tagger, Wall Mat Tag, Sync View; DRW Batch Run presets | ✔ | ✔ | ✔ |
| Other | Settings export/import round trip, About / update check, Controller + token broker, Diagnostic Report | — | — | — |

For every write row check: the model reopens, the change is present, nothing is left borrowed
(workshared), and a non-workshared cloud model was **saved** to the cloud rather than failing on a
sync call. Record each row as PASS, FAIL or SKIPPED with the reason — never drop one silently.

Things that make two runs differ without anything being wrong (mask them before diffing):
wall-clock columns (`Datetime`, `Datetime (Finish)`, `Run`), the run stamp in file names, the
Drawing Progress `[dp …ms …]` timing in the tracking Message, and row **order** in an auto-split
run, where parallel Revits append to shared logs. Detail CSVs are per-day append files, so empty
the job's destination folder before each run.

### Export IFC + Data in detail
Build the converter first: in `tools\ifc2frag` run `npm ci`, `npm run build` and
`.\fetch-node.ps1`, then build and install the add-in (Revit closed).

1. **NWD Helper → Export IFC + Data.** Create a set: output folder `C:\BimReport-test`, view
   filter blank (whole model), defaults otherwise. Save, pick a 1–2 model set, **Run**.
2. Expect `C:\BimReport-test\runs\<stamp>\<model>\` with `<model>.ifc`, `elements.csv`, `rooms.csv`,
   and `manifest-<pid>.json` beside the model folders. The tracking row says OK with element/room
   counts; the model is not modified and no element is borrowed.
3. Within a minute or two: `.frag` and `idmap.csv` appear next to each IFC, `convert-<pid>.json`
   says `converted`, and `C:\BimReport-test\latest\index.json` lists the models.
4. `history\rooms_gfa.csv` has one row per level per model; totals match a Revit room schedule.
5. GUID check: `node src\verify-guids.mjs <model.ifc> <elements.csv>` (informational — the report
   joins on ElementId through `idmap.csv`, which should list every exported building element).
6. **Schedule Run** with the same set and "Close Revit when complete": Revit closes, then the
   converter runs (see `runs\<stamp>\ifc2frag.log`: "waiting for process … exited").
7. Delete `IfcToFrag\` from the add-in folder and run again: IFC/CSV still written, and
   `ifc2frag.log` says the conversion was skipped.

## 3. Human click-through (about 15 minutes)
Automation constructs every dialog and runs every engine, but it does not click. In Revit 2026,
once with the Full edition and once with DRW Helper:

1. The ribbon tab and every panel appear; click each button once and confirm its dialog opens.
2. Last-used values are restored in each dialog on the second open.
3. In Cut Model, Initiate to Forma, Internal Relink, Correct Workset, Batch Link RVT, Export NWC,
   Export IFC + Data and Batch Interop (its checkset row): save a set, save it under a new name,
   overwrite it (confirm prompt), duplicate it, delete it (confirm prompt).
4. With a small local model open: tick "Run on active model only" in one simple check, Correct
   Workset, Export NWC, Export IFC + Data and Batch Link RVT. Each shows a result grid; nothing is
   saved or synced, and the exports write their files.
5. One interactive **Batch Run** with "leave files open".
6. **Edit Sets → Edit Model Set** with no model open: the tree loads your hubs, a folder lists its
   `.rvt` models, a saved set appears in every model-set list. If sign-in is missing, the dialog
   names the reason.
7. **GUID mapping, once per new hub:** open one model of a saved set normally and compare
   `Document.GetCloudModelPath()` → `GetProjectGUID()` / `GetModelGUID()` with the `ProjectGuid` /
   `ModelGuid` in the set's JSON under `%AppData%\KhBatchRunner\Presets\`. Trust batch runs on that
   hub only after this matches. A hand-authored set (`src\KhBatchRunner\docs\sample-preset.json`)
   is the fallback if the crawler mis-maps.

## Installer (.exe)
`install/Output/KhBatchRunner-Setup.exe` — **ONE installer for Revit 2024 + 2025 +
2026, all-users, requires admin**. Build it with
`powershell install\Build-Installer.ps1`, which runs the three Release builds
(`-p:RevitVersion=2024|2025|2026`), checks each per-year `bin\Release\<year>` tree,
then compiles `install/KhBatchRunner.iss` with `ISCC.exe`. The setup installs the
add-in only for the Revit years **detected** on the machine (each year is a
component, auto-checked when `C:\Program Files\Autodesk\Revit <year>` exists), into
`%ProgramData%\Autodesk\Revit\Addins\<year>\` — the machine-wide location that loads
for every user. This is deliberate: on managed/office laptops the installer is
elevated under a **separate admin account**, and a per-user install would have landed
in that admin's profile (`%AppData%`) — invisible to the logged-in user, so the
ribbon tab never appeared. One un-suffixed `AppId` → a single Add/Remove entry; the
installer also quietly uninstalls the legacy per-year 0.1.x installs first so a year
isn't double-registered. Unsigned, so SmartScreen shows "Windows protected your PC" →
**More info → Run anyway**. Close Revit before installing (the installer offers to
close it if the dll is locked).

Verify after install: the "KH-Tool" ribbon tab appears in each installed Revit
year, there is exactly one "KH Batch Script Runner for Autodesk Forma" entry in Add/Remove
Programs, any old per-year entries are gone, and uninstall removes the add-in from every year
folder. The gates rebuild the DLL without a version stamp, so build the installers **after** them.
