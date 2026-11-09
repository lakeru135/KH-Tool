# KH Batch Script Runner — Install & Test Guide

Status as of 2026-07-02: **built, deployed to Revit 2026, and loaded** (see
"Automated verification" below). The remaining steps need a human at Revit and,
for the real run, live Forma access.

## Build
```pwsh
# default target Revit 2026
dotnet build src\KhBatchRunner\KhBatchRunner.csproj -c Debug
# (Revit 2025 target requires Revit 2025 installed locally for its RevitAPI.dll)
dotnet build src\KhBatchRunner\KhBatchRunner.csproj -c Debug -p:RevitVersion=2025
```

## Manual install
```pwsh
# Close Revit first if the add-in is already loaded (the dll gets locked).
.\install\Install-Dev.ps1 -RevitVersion 2026
```
Copies `KhBatchRunner.dll` + `.addin` (+ `.pdb`) into
`%AppData%\Autodesk\Revit\Addins\2026\`. Start Revit → the **KH Batch** ribbon
tab appears.

## Automated verification (already done, no Revit UI needed)
- `dotnet build` succeeds, 0 errors.
- Reflection check (MetadataLoadContext) confirms the `.addin` manifest maps to
  real, public types: `KhBatchRunner.App : IExternalApplication` and
  `KhBatchRunner.Commands.GetPhaseWorksetCommand : IExternalCommand`.
- Deployed dll SHA-256 matches the built dll.
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

## Smoke test (human, no cloud needed) — DO THIS FIRST
A `smoke-test.json` preset with placeholder all-zero GUIDs is already at
`%AppData%\KhBatchRunner\Presets\`. Its models are *expected to fail to open* —
the point is to prove the ribbon → dialog → batch loop → CSV logger pipeline.

1. Start (or restart) Revit 2026. Confirm the **KH Batch** tab + **Get Phase &
   Workset** button appear.
2. Click the button → the run dialog opens.
3. Confirm the **Model preset** dropdown lists `Sample Preset (edit me)` and the
   **Export CSV log to** box has a default Desktop path.
4. Click **Run**.
5. Expect a summary dialog (Succeeded: 0, Failed: 2) and a CSV at the chosen path
   with one error row per model, `Datetime` + `Model Name` columns populated.
6. Run again → confirm the CSV **appends** (does not overwrite).

**Pass = the pipeline works.** Per-model failures on the dummy GUIDs are correct.

## Export IFC + Data (human, Revit + a test model set)
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
year, there is exactly one "KH Batch Script Runner" entry in Add/Remove Programs, any
old per-year entries are gone, and uninstall removes the add-in from every year folder.

## Crawler test (human + live Forma) — Browse Forma button
The **Browse Forma** button (Phase 1–2) creates presets from a real folder browse.
It works with **no model open** (Revit home screen), like Bird / Dynamo Multiplayer —
the token comes from the signed-in session, not from any open document. If auth
fails, the "no token" dialog now names the exact reason (e.g. "Not signed in").

1. Be **signed in to Forma in desktop Revit** (same account that owns the models);
   the add-in borrows the session token — no APS app needed.
2. Click **Browse Forma** → the tree loads your hubs. Expand hub → project →
   folders; select a folder to list its `.rvt` models on the right.
3. Check models → **Add checked** → give the preset a name → **Save preset**.
4. **VERIFY THE GUID MAPPING (first run only):** open one of those models in
   Revit normally and read `Document.GetCloudModelPath()` →
   `CloudPath.GetProjectGUID()` / `GetModelGUID()`; confirm they match the
   `ProjectGuid`/`ModelGuid` the crawler wrote into the preset JSON. This is the
   crawler's main risk — trust batch runs only after this matches once.
5. Then run **Get Phase & Workset** against the saved preset; confirm real
   phases/worksets rows and a clean error row for any model you can't access.

### Hand-authored preset (fallback if the crawler mis-maps)
Copy `src\KhBatchRunner\docs\sample-preset.json` to
`%AppData%\KhBatchRunner\Presets\<name>.json` and fill each model's `Region`,
`ProjectGuid`, `ModelGuid` from the `GetCloudModelPath()` values above.
