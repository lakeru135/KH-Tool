using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Batch;
using KhBatchRunner.Export;

namespace KhBatchRunner.Actions;

/// <summary>
/// Writes what the BIM report reads, one folder per model:
/// <c>&lt;output&gt;\runs\&lt;run stamp&gt;\&lt;model&gt;\</c> holds the <c>.ifc</c>, <c>elements.csv</c> and
/// <c>rooms.csv</c>; <c>&lt;output&gt;\history\rooms_gfa.csv</c> gains one row per level per model per
/// run. When the run ends, the bundled converter (<c>IfcToFrag\</c> beside this add-in) turns each IFC
/// into a That Open <c>.frag</c> plus <c>idmap.csv</c> and refreshes <c>&lt;output&gt;\latest\</c>.
///
/// <para><b>Category is Check, like Export NWC.</b> Nothing is written back to the model. Revit's IFC
/// exporter does need an open transaction (measured 2026-11-09: without one it throws
/// <c>ModificationOutsideTransactionException</c>), so the export runs inside one that is always
/// rolled back. On a Forma cloud model opened attached, that left the document unmodified and
/// borrowed no element.</para>
///
/// <para><b>One view, not several.</b> The report shows one model per discipline; a second IFC from
/// the same model would show up as a second model. The first 3D view (by name) whose title contains
/// the set's text is exported; blank exports the whole model.</para>
///
/// <para><b>The converter runs after Revit, not inside it.</b> Converting needs up to ~1 GB per
/// large model, and Revit may be holding tens of GB at the end of a run. On a scheduled run the
/// converter is started with this Revit's process id and waits for it to exit; on an interactive run
/// it starts straight away at below-normal priority.</para>
/// </summary>
public sealed class ExportIfcDataAction : BatchActionBase
{
    public const string DisplayName = ActionNames.ExportIfcData;

    /// <summary>Statically reachable posture — see <see cref="ExportNwcAction.DeclaredWorksets"/>.</summary>
    public const WorksetPosture DeclaredWorksets = SyntheticActionPostures.ExportIfcData;

    private readonly ExportIfcInputSet _spec;
    private string _root = "";
    private string _runStamp = "";
    private string _runFolder = "";
    private string _note = "";
    private string _worksetPlan = "";
    private readonly HashSet<string> _usedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ManifestModel> _manifest = new();

    public ExportIfcDataAction(ExportIfcInputSet spec) => _spec = spec;

    public override string Name => DisplayName;

    public override IReadOnlyList<string> Columns { get; } = new[]
        { "IFC File", "Scope", "View Used", "IFC Size (MB)", "Elements", "Rooms", "Placed Rooms m²", "Folder", "Contents" };

    public override string PerModelNote => _note;

    /// <summary>Background export, one model open at a time — same reasoning as
    /// <see cref="ExportNwcAction.NeedsActiveDocument"/>.</summary>
    public override bool NeedsActiveDocument => false;

    public override WorksetPosture Worksets => DeclaredWorksets;

    public override string WorksetPostureReason =>
        "Exports model geometry and element data, so a closed open writes an empty IFC and reports " +
        "success. Opens the worksets its export view shows (the Export NWC rule), or every workset " +
        "for a whole-model export.";

    /// <summary>The worksets the export view shows; every workset for a whole-model export or on any
    /// doubt. Links are never exported here, so worksets proven to hold only links stay shut.</summary>
    public override ICollection<WorksetId>? SelectWorksetsToOpen(Document doc)
    {
        List<WorksetId> all = ViewWorksetPlanner.AllUserWorksets(doc);
        try
        {
            if (string.IsNullOrWhiteSpace(_spec.ViewTitleContains))
                return Planned(all, $"all {all.Count} workset(s) — whole-model export");

            View3D? view = ViewWorksetPlanner.FindViews(doc, _spec.ViewTitleContains).FirstOrDefault();
            if (view is null)
                return Planned(all, $"all {all.Count} workset(s) — no matching view");

            List<WorksetId>? wanted = ViewWorksetPlanner.VisibleWorksets(
                doc, new[] { view }, ViewWorksetPlanner.LinkOnlyWorksets(), out int skipped);
            if (wanted is null)
                return Planned(all, $"all {all.Count} workset(s) — no workset is visible");

            string plan = $"{wanted.Count} of {all.Count} workset(s), from view '{view.Name}'";
            if (skipped > 0) plan += $", skipping {skipped} proven link-only";
            return Planned(wanted, plan);
        }
        catch (Exception ex)
        {
            return Planned(all, $"all {all.Count} workset(s) — the view could not be read: {ex.Message}");
        }
    }

    private List<WorksetId> Planned(List<WorksetId> chosen, string plan)
    {
        _worksetPlan = plan;
        Diagnostics.RunTrace.Mark($"{DisplayName} | workset plan: {plan}");
        return chosen;
    }

    public override void ConfigureOutput(string destinationFolder, string projectLogName)
    {
        _root = _spec.ResolveOutputFolder(destinationFolder);
        if (string.IsNullOrWhiteSpace(_root))
            throw new InvalidOperationException(
                "Export IFC + Data has nowhere to write: set an output folder in the options set, or " +
                "give the run a destination folder.");

        // The run stamp is shared by every pass and every split group of one run, so all of a run's
        // models land in the same runs\<stamp> folder whichever Revit exported them.
        _runStamp = (RunStamp ?? DateTime.Now).ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        _runFolder = Path.Combine(_root, "runs", _runStamp);
        Directory.CreateDirectory(_runFolder);
        Directory.CreateDirectory(Path.Combine(_root, "history"));
    }

    public override IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(
        UIApplication uiApp, Document doc)
    {
        _note = "";
        string modelName = ModelFileName(doc.Title);
        string folderName = UniqueFolder(SafeFileName(modelName));
        string folder = Path.Combine(_runFolder, folderName);
        var entry = new ManifestModel { Name = modelName, Folder = folder, Status = "failed" };
        _manifest.Add(entry);

        try
        {
            Directory.CreateDirectory(folder);
            View3D? view = null;
            if (!string.IsNullOrWhiteSpace(_spec.ViewTitleContains))
            {
                List<View3D> matches = ViewWorksetPlanner.FindViews(doc, _spec.ViewTitleContains);
                view = matches.FirstOrDefault();
                if (view is null)
                    _note = $"no 3D view title contains '{_spec.ViewTitleContains}' — exported the whole model instead";
                else if (matches.Count > 1)
                    _note = $"{matches.Count} views match — exported '{view.Name}' (first by name)";
            }

            if (!HasExportableGeometry(doc, view))
                throw new InvalidOperationException(
                    $"Nothing to export: {(view is null ? "this model" : $"view '{view.Name}'")} holds no model geometry. " +
                    $"Workset plan: {_worksetPlan}.");

            string ifcName = folderName + ".ifc";
            var sw = Stopwatch.StartNew();
            ExportIfc(doc, folder, folderName, view);
            var ifc = new FileInfo(Path.Combine(folder, ifcName));
            if (!ifc.Exists)
                throw new InvalidOperationException($"Revit reported no error but no IFC appeared at {ifc.FullName}.");
            Diagnostics.RunTrace.Mark($"{DisplayName} | {modelName} | IFC {ifc.Length / 1048576.0:F1} MB in {sw.Elapsed.TotalSeconds:F0}s");

            entry.Ifc = ifcName;
            int elements = 0;
            if (_spec.WriteElementsCsv)
            {
                elements = ModelDataWriter.WriteElements(doc, view, Path.Combine(folder, "elements.csv"), Extra);
                entry.ElementsCsv = "elements.csv";
            }

            List<LevelArea> levels = new();
            int roomRows = 0;
            if (_spec.WriteRoomsCsv)
            {
                levels = ModelDataWriter.WriteRooms(doc, Path.Combine(folder, "rooms.csv"), Extra);
                entry.RoomsCsv = "rooms.csv";
                roomRows = levels.Sum(l => l.Rooms);
                AppendGfaHistory(modelName, levels);
                if (roomRows == 0 && string.IsNullOrEmpty(_note))
                    _note = "no placed rooms found (check the rooms' workset is open)";
            }

            entry.Status = "exported";
            return new[]
            {
                (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["IFC File"] = ifcName,
                    ["Scope"] = view is null ? "Whole model" : "3D view",
                    ["View Used"] = view?.Name ?? "",
                    ["IFC Size (MB)"] = (ifc.Length / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture),
                    ["Elements"] = elements.ToString(CultureInfo.InvariantCulture),
                    ["Rooms"] = roomRows.ToString(CultureInfo.InvariantCulture),
                    ["Placed Rooms m²"] = levels.Sum(l => l.AreaM2).ToString("0.##", CultureInfo.InvariantCulture),
                    ["Folder"] = folder,
                    ["Contents"] = $"workset plan: {_worksetPlan}; " + Schedule.InstanceBudget.FreeMemoryNote(),
                }
            };
        }
        catch (Exception ex)
        {
            entry.Error = ex.Message;
            throw;
        }
    }

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc) =>
        throw new NotSupportedException("Export IFC + Data writes its row from RunDetailRows.");

    /// <summary>
    /// Revit's IFC export, inside a transaction that is always rolled back: the exporter refuses to
    /// run without one, and nothing it touches may reach the model.
    /// </summary>
    private void ExportIfc(Document doc, string folder, string baseName, View3D? view)
    {
        var options = new IFCExportOptions
        {
            FileVersion = _spec.Schema switch
            {
                IfcSchemaChoice.Ifc2x3CoordinationView2 => IFCVersion.IFC2x3CV2,
                IfcSchemaChoice.Ifc4DesignTransferView => IFCVersion.IFC4DTV,
                _ => IFCVersion.IFC4RV,
            },
            ExportBaseQuantities = _spec.ExportBaseQuantities,
            WallAndColumnSplitting = false,
        };
        if (view != null)
        {
            options.FilterViewId = view.Id;
            options.AddOption("VisibleElementsOfCurrentView", "true");
        }
        options.AddOption("ExportIFCCommonPropertySets", _spec.ExportCommonPropertySets ? "true" : "false");
        options.AddOption("ExportInternalRevitPropertySets", _spec.ExportRevitPropertySets ? "true" : "false");
        // Revit IFC exporter's SiteTransformBasis: 0 = shared coordinates, 3 = internal origin.
        options.AddOption("SitePlacement", _spec.UseSharedCoordinates ? "0" : "3");

        using var t = new Transaction(doc, "Export IFC (rolled back)");
        t.Start();
        try
        {
            doc.Export(folder, baseName, options);
        }
        finally
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
        }
    }

    private IReadOnlyList<string> Extra =>
        (_spec.ExtraParameters ?? new List<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();

    /// <summary>history\rooms_gfa.csv — appended under the cross-process log lock, because the groups
    /// of a split run write to it from separate Revits.</summary>
    private void AppendGfaHistory(string model, List<LevelArea> levels)
    {
        if (levels.Count == 0) return;
        string path = Path.Combine(_root, "history", "rooms_gfa.csv");
        var sb = new StringBuilder();
        foreach (LevelArea l in levels)
            sb.Append(_runStamp).Append(',')
              .Append(Logging.CsvFormat.Escape(model)).Append(',')
              .Append(Logging.CsvFormat.Escape(l.Level)).Append(',')
              .Append(l.Rooms.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(l.AreaM2.ToString("0.###", CultureInfo.InvariantCulture)).Append("\r\n");

        Logging.LogFileLock.Write(path, () =>
        {
            bool isNew = !File.Exists(path);
            using var w = new StreamWriter(path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: isNew));
            if (isNew) w.Write("RunStamp,Model,Level,RoomCount,Area_m2\r\n");
            w.Write(sb.ToString());
        });
    }

    /// <summary>
    /// After the last model: write this process's manifest and hand it to the converter. Each Revit of
    /// a split run writes its own manifest (named by process id) and starts its own converter; the
    /// converter serialises the shared latest\ update with a lock file.
    /// </summary>
    public override void OnRunComplete()
    {
        if (_manifest.Count == 0 || string.IsNullOrEmpty(_runFolder)) return;

        int pid = Process.GetCurrentProcess().Id;
        string manifestPath = Path.Combine(_runFolder, $"manifest-{pid}.json");
        var manifest = new
        {
            schema = 1,
            runStamp = _runStamp,
            outputRoot = _root,
            keepRunsDays = _spec.KeepRunsDays,
            producedBy = $"{Product.Name} {Product.Version}",
            models = _manifest.Select(m => new
            {
                name = m.Name, folder = m.Folder, ifc = m.Ifc, status = m.Status,
                elementsCsv = m.ElementsCsv, roomsCsv = m.RoomsCsv, error = m.Error,
            }),
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        if (_spec.ConvertToFrag) StartConverter(manifestPath, pid);
    }

    private void StartConverter(string manifestPath, int pid)
    {
        string dir = Path.Combine(Path.GetDirectoryName(typeof(ExportIfcDataAction).Assembly.Location) ?? "", "IfcToFrag");
        string node = Path.Combine(dir, "node.exe");
        string script = Path.Combine(dir, "ifc2frag.cjs");
        string log = Path.Combine(_runFolder, "ifc2frag.log");

        if (!File.Exists(node) || !File.Exists(script))
        {
            string msg = $"FRAG conversion skipped: the converter is not installed ({dir}). The IFC files are complete.";
            Diagnostics.RunTrace.Mark($"{DisplayName} | {msg}");
            TryAppend(log, msg);
            return;
        }

        // Unattended (scheduled or a split group): convert once THIS Revit has exited.
        string waitArg = Schedule.ScheduleEnvironment.IsUnattendedLaunch ? $" --wait-pid {pid}" : "";
        try
        {
            // ShellExecute, hidden: the converter must outlive Revit and inherit none of its handles.
            var psi = new ProcessStartInfo(node, $"\"{script}\" --manifest \"{manifestPath}\"{waitArg}")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = dir,
            };
            using Process? p = Process.Start(psi);
            try { if (p != null) p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* best effort */ }
            Diagnostics.RunTrace.Mark($"{DisplayName} | converter started for {Path.GetFileName(manifestPath)}{waitArg}");
        }
        catch (Exception ex)
        {
            string msg = "FRAG conversion could not start: " + ex.Message;
            Diagnostics.RunTrace.Mark($"{DisplayName} | {msg}");
            TryAppend(log, msg);
        }
    }

    private static void TryAppend(string path, string line)
    {
        try { File.AppendAllText(path, $"{DateTime.UtcNow:O} {line}\r\n"); } catch { /* best effort */ }
    }

    /// <summary>Same test as Export NWC: any non-view-specific MODEL element in scope. Unreadable means yes.</summary>
    private static bool HasExportableGeometry(Document doc, View3D? view)
    {
        try
        {
            FilteredElementCollector scope = view is null
                ? new FilteredElementCollector(doc)
                : new FilteredElementCollector(doc, view.Id);
            return scope.WhereElementIsNotElementType()
                .Any(e => !e.ViewSpecific && e.Category is { CategoryType: CategoryType.Model });
        }
        catch { return true; }
    }

    /// <summary>Two models with the same file name (different folders) must not share a folder.</summary>
    private string UniqueFolder(string name)
    {
        string candidate = name;
        for (int n = 2; _usedFolders.Contains(candidate) || Directory.Exists(Path.Combine(_runFolder, candidate)); n++)
            candidate = $"{name} ({n})";
        _usedFolders.Add(candidate);
        return candidate;
    }

    private static string ModelFileName(string title)
    {
        string name = title;
        const string detached = "_detached";
        if (name.EndsWith(detached, StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - detached.Length);
        if (name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - 4);
        return name;
    }

    private static string SafeFileName(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Trim();

    private sealed class ManifestModel
    {
        public string Name { get; set; } = "";
        public string Folder { get; set; } = "";
        public string Ifc { get; set; } = "";
        public string Status { get; set; } = "";
        public string ElementsCsv { get; set; } = "";
        public string RoomsCsv { get; set; } = "";
        public string Error { get; set; } = "";
    }
}
