# KH-Tool

Batch run, schedule and check Revit models stored in **Autodesk Forma** (formerly ACC / BIM 360).

**Works with:** Revit 2024 · 2025 · 2026 · 2027 · Autodesk Forma · BIM 360

**New here?** Read the [KH-Tool handbook](https://claude.ai/artifact/CXSCchFSkXbCvrGTcaUpfw)
(its source is kept in [`docs/handbook.html`](docs/handbook.html)): install,
first run, what each button does and what to do when a run goes wrong.

KH-Tool is a C# Revit add-in from Kanghun BIM. It browses your Forma hubs and saves reusable **model
sets**, either as a fixed list or as folders that are re-scanned at run time. It then **batch-runs**
read-only checks, write actions, Forma operations and Dynamo `.dyn` script sets across many cloud
or local models. Runs can be interactive or recurring, through Windows Task Scheduler. One source
tree builds for every supported Revit year.

> **Test on copies first.** Several batch actions write to the model and sync it to central.

## Products and editions

One codebase builds three products, chosen with `-p:Edition=`:

| Edition | Assembly / ribbon tab | What it is |
|---|---|---|
| `Full` (default) | `KhBatchRunner` / **KH-Tool** | The full toolset, with the build-validity gate (below). |
| `FullUnlimited` | same as Full | Full with the validity gate compiled out. Same AddInId and installer AppId, so it replaces a gated install. |
| `DRW` | `DrwHelper` / **DRW Helper** | The drawing-production tools on their own. |

## Features

- **Forma browser + model/folder sets.** Pick models across projects and save named sets, or point
  a set at one or more Forma folders that are re-scanned every run.
- **Multi Model Checks** (read-only). Phase & Workset, Site Location & PBP, Design Option, Get
  Link Workset, Drawing Progress, Sheet Publish, Rebuild Report and more. Each produces a CSV per
  model plus cross-model Excel workbooks.
- **Forma Batch Action.** Initiate to Forma, Cut Model, Internal Relink, Get File Log, Get Data
  Extract, Check Sheet Publish.
- **Model Batch Action** (writes to the model). Purge Import CAD, Isolate Warning Elements,
  Batch Link RVT, Correct Workset, Batch Interop (runs the Autodesk Model Checker).
  Each can sync or dry-run.
- **Batch Run / Schedule Run.** Combine sets and ad-hoc models with any ordered list of actions and
  Dynamo script sets, then run now or save as a recurring Windows Task Scheduler job.
  - Large runs are split across parallel Revit instances. Group size and the instance cap come from
    the model count, RAM and CPU, and every group writes to one combined log.
  - Batch Interop never splits: it always runs in one supervised Revit.
- **Recovery and notifications.** A supervisor re-runs missing model results a bounded number of
  times. Results are posted to Windows Notification Center. Revit is never force-closed: a stalled
  process is left open for inspection.
- **DRW Helper.** Enlarge Room / Stair, Datum Dimension, Stair Annotation, Single Font, Preset
  Tagger, Wall Mat Tag, Enlarge Door & Window.
- **Parameter Helper, Mirror Check, Excel Link** (schedules to a workbook and back, as one undo
  step), **NWD Helper** (builds a parametric `.rfa` from an export made by the separate KH-NWD Tool
  Navisworks add-in).
- **Export IFC + Data** (NWD Helper panel, Batch Run, Schedule Run). One IFC per model from a named
  3D view, plus `elements.csv` and `rooms.csv`, in a dated `runs\<stamp>\` folder; the bundled
  converter (`tools/ifc2frag`, That Open fragments) then writes `.frag` + `idmap.csv` and refreshes
  `latest\` for a web viewer. Read-only: the export runs in a rolled-back transaction. Build the
  converter with `npm ci && npm run build` and `fetch-node.ps1` in `tools/ifc2frag` before building
  the add-in, or the action exports IFC/CSV only and says the conversion was skipped.
- **KH-Tool AI Bridge** (`KhMcp`). An MCP server that lets an AI client run KH-Tool's own tools in
  the open Revit model. Read [SECURITY.md](SECURITY.md) before enabling **AI Code**.

## Build

Requires the **.NET 8 SDK**, and the **.NET 10 SDK** to build for Revit 2027. The Revit API comes either from a local Revit install or from NuGet
(`-p:RevitApiFromNuGet=true`, which is useful on a PC without that Revit year).

```pwsh
# Revit 2026, Full edition (defaults)
dotnet build src/KhBatchRunner/KhBatchRunner.csproj -c Release -p:RevitVersion=2026

# other years / editions
dotnet build src/KhBatchRunner/KhBatchRunner.csproj -p:RevitVersion=2024 -p:RevitApiFromNuGet=true
dotnet build src/KhBatchRunner/KhBatchRunner.csproj -p:RevitVersion=2027 -p:RevitApiFromNuGet=true
dotnet build src/KhBatchRunner/KhBatchRunner.csproj -p:RevitVersion=2026 -p:Edition=DRW
dotnet build src/KhBatchRunner/KhBatchRunner.csproj -p:RevitVersion=2026 -p:Edition=FullUnlimited

# the standalone controller (no Revit reference)
dotnet build src/KhBatchController/KhBatchController.csproj
```

Output goes to `src/KhBatchRunner/bin/<Config>/[<Edition>/]<Year>/`. Revit 2024 builds target
net48; 2025 and 2026 target net8; 2027 targets net10.

## Install

- **Installer (all users, admin).** `.\install\Build-Installer.ps1` builds
  `install\Output\KhBatchRunner-Setup.exe`, which installs for every Revit year it detects.
  `-Unlimited` builds the ungated variant.
- **Dev install (per user, no admin).** `.\install\Install-Dev.ps1 -RevitVersion 2026`. Close Revit
  first. A per-user copy and an all-users copy share one AddInId, so remove one of them.
- **Upgrading from 1.x** (named "ACC Batch Script Runner" before 2.0.0): the 2.0.0 installer
  replaces it in place. On first start, settings and model sets are copied from the old
  `%AppData%` folder, which is kept as a backup. Each scheduled task is re-registered under its new
  name the first time Revit starts interactively.

### Build validity and update check

Every gated build checks `latest.json` on this repo's `main` branch once a day. It learns whether a
newer version exists and whether this build may still run. The manifest can retire a version, set a
minimum version or move the global date. If the check can't reach the manifest or parse it, the build
falls back to the date compiled into it and never blocks anyone. See `Update/UpdateCheck.cs`.

When cutting a release, keep `Product.Version` (`Product.cs`) in step with `AppVersion` in the
`.iss` files, and set `BuiltInExpiryUtc` to 90 days after the build date.

## Test gates

Run these before a release. The last two rebuild the DLL without a version stamp, so **build the
installers after them**.

| Gate | What it proves |
|---|---|
| `install\Verify-NoClientNames.ps1` | No client, project or site names in tracked files |
| `install\Verify-NoForcedRevitClose.ps1` | Nothing shipped terminates Revit |
| `install\Verify-ConstrainedLanguageSafe.ps1` | Installed `.ps1` files run under locked-down PowerShell |
| `install\Verify-WindowConstruction.ps1` | Every dialog constructs, stays readable at its minimum size, and round-trips a schedule job |
| `install\Verify-ActionPosture.ps1` | No action compiles without declaring its workset posture |
| `install\Verify-BatchFeatureContract.ps1 -Inject` | Every batch dialog shares the save-set row, the model-set list and the folder default (see CONTRIBUTING.md) |

The first three also run from `Build-Installer.ps1`. Offline harnesses live in `tools/`:
`LivenessSim` (run-splitting and liveness rules), `ThemePreview` (renders and layout-checks every
dialog outside Revit), `BundleTest`, `DimInk` and `interopbench`.

## Repo layout

- `src/KhBatchRunner/` is the add-in: Commands, UI, Batch engine, Actions, Schedule, Forma, Drw,
  Interop, Mcp.
- `src/KhBatchLauncher/` is the scheduled-task launcher and notification helper.
- `src/KhBatchController/` is the standalone controller.
- `src/KhMcp/` is the MCP server for the AI Bridge.
- `install/` holds the Inno Setup scripts and the build/verify PowerShell.
- `tools/` holds the offline test harnesses.

## License, trademarks, brand

- **License.** [MIT](LICENSE), with no warranty: you use it at your own risk.
- **Trademarks.** Autodesk, Revit, Forma, ACC, BIM 360 and Navisworks are trademarks of Autodesk,
  Inc. This project is not affiliated with or endorsed by Autodesk.
- **Brand.** The MIT license covers the code, not the Kanghun / KH-Tool name or logo. Forks must
  not use that name or logo.
