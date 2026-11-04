using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Logging;

namespace KhBatchRunner.Actions;

/// <summary>
/// Multi Model Action: read every warning in the model, isolate each warning's failing
/// elements into its own "Model Check_W-##_&lt;description&gt;" 3D view — a batch port of
/// the team's "Model QA_Get Warning Element and create 3D views.dyn" graph. Warning
/// descriptions are numbered W-01, W-02… in a master index CSV
/// (<see cref="ConfigureOutput"/>) so the same description always gets the same code, even
/// across separate runs and models. Write-capable (<see cref="ActionCategory.Action"/>) —
/// creating/deleting views is a model change, so <see cref="Batch.BatchEngine"/> syncs/saves
/// afterwards per its persistence rule.
/// </summary>
public sealed class IsolateWarningElementsAction : BatchActionBase
{
    // View names this action creates/recognizes, so a re-run can find and refresh them:
    // "Model Check_W-01_Walls overlap" → code group "W-01".
    private static readonly Regex CheckViewName = new(@"^Model Check_(W-\d+)_", RegexOptions.Compiled);

    // A coded view that couldn't be deleted (it was the active view — see DoIsolate) gets
    // renamed to this instead, so it stops colliding with the freshly recreated view. It no
    // longer matches CheckViewName, so a later run's sweep picks it up once it's no longer active.
    private const string StaleViewPrefix = "Model Check_stale_";

    // Characters Revit rejects in a view name, per the team script.
    private static readonly char[] IllegalViewNameChars = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

    // Warning description → "W-##", loaded from the sidecar on ConfigureOutput and kept
    // stable for the lifetime of this action instance (i.e. for the whole batch run).
    private readonly Dictionary<string, string> _descToCode = new();
    private int _nextNumber = 1;
    private string? _sidecarPath;

    // Roll-up for the most recently processed model, surfaced in that model's tracking-log
    // Message cell (the original graph's "general log" — the per-element counts alone don't
    // show how many 3D views were created/updated/swept). Set at the end of every DoIsolate;
    // the engine reads it immediately after, one model per tick, so a plain field is safe.
    private string _perModelNote = "";

    public override string Name => "Isolate Warning Elements";

    public override ActionCategory Category => ActionCategory.Action;

    public override string PerModelNote => _perModelNote;

    public override IReadOnlyList<string> Columns { get; } = new[]
        { "Warning Index", "Warning", "Element Id", "Category", "Family", "Type", "3D View" };

    public override WorksetPosture Worksets => WorksetPosture.PartialByDesign;

    public override string WorksetPostureReason =>
        "Works from doc.GetWarnings() and resolves each failing element, plus a Grid collector for " +
        "the isolation views — and a warning about elements Revit has not loaded is not in the " +
        "list. So the views it builds cover the warnings the closed open could see, and a model " +
        "with none reads as a model with no warnings. Found and ACCEPTED by the user on " +
        "2026-08-16; the fix is OpensAll, since a warning can name any element.";

    /// <summary>
    /// Locates (and loads) the master warning-index CSV in the destination folder root —
    /// called once by <see cref="Batch.BatchEngine.BeginRun"/>, before any model opens, so
    /// the W-## numbering is stable across every model in this run. If the folder can't be
    /// created/read, numbering still works for this run — it just isn't persisted/shared
    /// with other runs (fail-soft, same spirit as the CSV loggers).
    /// </summary>
    public override void ConfigureOutput(string destinationFolder, string projectLogName)
    {
        try
        {
            Directory.CreateDirectory(destinationFolder);
            string safe = CsvFormat.SanitizeFileName($"{projectLogName}_Warnings Index");
            _sidecarPath = Path.Combine(destinationFolder, safe + ".csv");
        }
        catch
        {
            _sidecarPath = null;
        }
        LoadIndex();
    }

    // The engine's ProcessModel only ever calls RunDetailRows per model (Run() exists for
    // callers that want a single summary row instead), so the warnings must be walked and
    // the views created in exactly one of the two. DoIsolate() does the real work once;
    // both members just read its result.
    public override IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(UIApplication uiApp, Document doc) =>
        DoIsolate(doc).Rows;

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        var result = DoIsolate(doc);
        return new Dictionary<string, string>
        {
            ["Total Warnings"] = result.TotalWarnings.ToString(),
            ["Total Elements"] = result.Rows.Count.ToString(),
            ["New Views"] = result.NewViews.ToString(),
            ["Modified Views"] = result.ModifiedViews.ToString(),
            ["Deleted Views"] = result.DeletedViews.ToString()
        };
    }

    private readonly record struct IsolateResult(
        List<IReadOnlyDictionary<string, string>> Rows, int TotalWarnings,
        int NewViews, int ModifiedViews, int DeletedViews);

    private IsolateResult DoIsolate(Document doc)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();

        // Warning description → (element id → category/family/type), aggregated across
        // every FailureMessage that shares that description in this model.
        var descOrder = new List<string>();
        var descToElements = new Dictionary<string, Dictionary<ElementId, (string Category, string Family, string Type)>>();
        int totalWarnings = 0;

        using var t = new Transaction(doc, "Isolate Warning Elements");
        t.Start();

        foreach (FailureMessage warning in doc.GetWarnings())
        {
            try
            {
                totalWarnings++;
                // Descriptions are newline-normalized right here, the single point every
                // other use (index key, sidecar row, detail rows, view name) reads from — so
                // the sidecar stays physically one line per row. Without this, a multi-line
                // RFC4180-quoted description would split across lines that LoadIndex's
                // File.ReadAllLines-based parser can't reassemble, silently dropping it and
                // re-numbering that warning with a fresh W-## every run.
                string desc = NormalizeDescription(warning.GetDescriptionText());
                if (string.IsNullOrWhiteSpace(desc)) desc = "(no description)";

                if (!descToElements.TryGetValue(desc, out Dictionary<ElementId, (string, string, string)>? elements))
                {
                    elements = new Dictionary<ElementId, (string, string, string)>();
                    descToElements[desc] = elements;
                    descOrder.Add(desc);
                }

                foreach (ElementId failingId in warning.GetFailingElements())
                {
                    Element? el = doc.GetElement(failingId);
                    if (el == null) continue;

                    (Element target, bool wasType) = ResolveIsolationTarget(doc, el);
                    if (elements.ContainsKey(target.Id)) continue; // already recorded under this description

                    string category = target.Category?.Name ?? "";
                    string family, typeName;
                    if (wasType && el is ElementType typeEl)
                    {
                        family = typeEl.FamilyName;
                        typeName = typeEl.Name;
                    }
                    else
                    {
                        ElementType? et = doc.GetElement(target.GetTypeId()) as ElementType;
                        family = et?.FamilyName ?? "";
                        typeName = et?.Name ?? "";
                    }

                    elements[target.Id] = (category, family, typeName);
                }
            }
            catch
            {
                // One bad warning (e.g. a stale failing-element id) shouldn't stop the rest.
            }
        }

        // Assign/lookup a stable W-## for every description seen, even ones that ended up
        // with zero isolatable elements — so a description never "skips" a number just
        // because this particular model couldn't resolve any of its failing elements.
        var descToCode = new Dictionary<string, string>();
        foreach (string desc in descOrder)
            descToCode[desc] = GetOrAssignCode(desc);

        // --- refresh the "Model Check_W-##_…" views (+ sweep any stale leftovers) ---------
        List<View3D> existingCheckViews = new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .Where(v => !v.IsTemplate &&
                        (CheckViewName.IsMatch(v.Name) || v.Name.StartsWith(StaleViewPrefix, StringComparison.Ordinal)))
            .ToList();

        var existingCodes = new HashSet<string>();
        foreach (View3D v in existingCheckViews)
        {
            Match m = CheckViewName.Match(v.Name);
            if (m.Success) existingCodes.Add(m.Groups[1].Value);
        }

        // Per code that had a view before this pass, whether that view was actually removed
        // (deleted, or renamed out of the way because it was the active view) — tracked
        // instead of assumed, because Revit refuses to delete the active view and
        // OpenAndActivateDocument can restore one of these check views as active. Assuming
        // every delete succeeds would both overcount "deleted" and, worse, leave the old view
        // sitting on the exact name the fresh one below needs — a colliding "(2)" duplicate
        // that never gets cleaned up since the same view tends to stay active every run.
        var codeFullyRemoved = existingCodes.ToDictionary(c => c, _ => true);
        ElementId? activeViewId = doc.ActiveView?.Id;

        foreach (View3D v in existingCheckViews)
        {
            Match m = CheckViewName.Match(v.Name);
            string? code = m.Success ? m.Groups[1].Value : null;
            bool isActive = activeViewId != null && v.Id == activeViewId;

            if (isActive)
            {
                if (code != null)
                {
                    // Can't delete it — rename it out of the way so the fresh view for this
                    // code (created below, if still needed) gets the clean name instead of
                    // colliding with this one.
                    try { v.Name = MakeStaleName(); }
                    catch { codeFullyRemoved[code] = false; }
                }
                continue; // still around either way — not a removal, don't recurse into it further.
            }

            try
            {
                doc.Delete(v.Id);
            }
            catch
            {
                // Not fatal — leave it, it'll be retried next run. If it belonged to a code,
                // that code's view didn't actually go away, so don't count it as deleted below.
                if (code != null) codeFullyRemoved[code] = false;
            }
        }

        List<string> activeDescs = descOrder.Where(d => descToElements[d].Count > 0).ToList();
        HashSet<string> activeCodes = activeDescs.Select(d => descToCode[d]).ToHashSet();
        int deletedViews = existingCodes.Except(activeCodes).Count(c => codeFullyRemoved[c]);
        int newViews = 0, modifiedViews = 0;

        ViewFamilyType? vft3D = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);

        ICollection<ElementId> gridIds = new FilteredElementCollector(doc).OfClass(typeof(Grid)).ToElementIds();

        foreach (string desc in activeDescs)
        {
            string code = descToCode[desc];
            Dictionary<ElementId, (string Category, string Family, string Type)> elements = descToElements[desc];
            if (existingCodes.Contains(code)) modifiedViews++; else newViews++;

            string viewName = "";
            try
            {
                if (vft3D == null)
                    throw new InvalidOperationException("No 3D view family type available in this model.");

                View3D view = View3D.CreateIsometric(doc, vft3D.Id);
                viewName = AssignUniqueViewName(view, code, desc);

                if (doc.IsWorkshared)
                {
                    var worksetIds = new HashSet<WorksetId>();
                    foreach (ElementId id in elements.Keys)
                    {
                        Element? el = doc.GetElement(id);
                        if (el != null) worksetIds.Add(el.WorksetId);
                    }
                    foreach (WorksetId wsId in worksetIds)
                    {
                        try { view.SetWorksetVisibility(wsId, WorksetVisibility.Visible); }
                        catch { /* some worksets (e.g. the view's own) can't be toggled */ }
                    }
                }

                try
                {
                    var toIsolate = new List<ElementId>(elements.Keys);
                    toIsolate.AddRange(gridIds);
                    view.IsolateElementsTemporary(toIsolate);
                    view.ConvertTemporaryHideIsolateToPermanent();
                }
                catch
                {
                    // The view still exists and the rows below are still meaningful even if
                    // the isolate step itself failed (e.g. an element type that can't hide).
                }
            }
            catch
            {
                // View creation failed outright — still emit rows with a blank 3D View column
                // rather than losing the warning data for this description.
            }

            foreach ((ElementId id, (string category, string family, string type)) in elements)
            {
                rows.Add(new Dictionary<string, string>
                {
                    ["Warning Index"] = code,
                    ["Warning"] = desc,
                    ["Element Id"] = id.Value.ToString(),
                    ["Category"] = category,
                    ["Family"] = family,
                    ["Type"] = type,
                    ["3D View"] = viewName
                });
            }
        }

        t.Commit();

        _perModelNote =
            $"{totalWarnings} warning(s), {rows.Count} element(s); " +
            $"3D views: {newViews} new / {modifiedViews} updated / {deletedViews} removed";
        return new IsolateResult(rows, totalWarnings, newViews, modifiedViews, deletedViews);
    }

    /// <summary>
    /// Some warnings point at a Type (e.g. "family type will be deleted") rather than an
    /// instance — a Type has no type-of-its-own (<see cref="Element.GetTypeId"/> is invalid)
    /// and exposes a <see cref="ElementType.FamilyName"/>, unlike an instance. A Type can't
    /// be isolated in a 3D view, so find one instance of that type to stand in for it.
    /// </summary>
    private static (Element Target, bool WasType) ResolveIsolationTarget(Document doc, Element el)
    {
        bool looksLikeType = el.GetTypeId() == ElementId.InvalidElementId
                             && el is ElementType typeEl && !string.IsNullOrEmpty(typeEl.FamilyName);
        if (!looksLikeType)
            return (el, false);

        Element? instance = FindFirstInstanceOfType(doc, el.Id);
        return (instance ?? el, true);
    }

    private static Element? FindFirstInstanceOfType(Document doc, ElementId typeId)
    {
        try
        {
            var provider = new ParameterValueProvider(new ElementId(BuiltInParameter.ELEM_TYPE_PARAM));
            var rule = new FilterElementIdRule(provider, new FilterNumericEquals(), typeId);
            var filter = new ElementParameterFilter(rule);
            Element? found = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(filter)
                .FirstElement();
            if (found != null) return found;
        }
        catch
        {
            // Some categories don't support ELEM_TYPE_PARAM in a fast filter — fall through
            // to the slower scan below instead of giving up on this warning.
        }

        return new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .FirstOrDefault(e => e.GetTypeId() == typeId);
    }

    /// <summary>
    /// Collapses any CR/LF in a raw warning description to a single space (and squashes the
    /// resulting run of whitespace) so the description is a genuine one-liner everywhere it's
    /// used from this point on — the sidecar CSV in particular relies on this invariant,
    /// since <see cref="LoadIndex"/> reads it back with a plain per-line parser.
    /// </summary>
    private static string NormalizeDescription(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string collapsed = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        return Regex.Replace(collapsed, @"\s+", " ").Trim();
    }

    /// <summary>A throwaway name for a coded view that couldn't be deleted (see <see cref="DoIsolate"/>).</summary>
    private static string MakeStaleName() => StaleViewPrefix + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Strips characters Revit rejects in a view name, collapses newlines, and caps length.</summary>
    private static string CleanForViewName(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '\n' || c == '\r') { sb.Append(' '); continue; }
            sb.Append(Array.IndexOf(IllegalViewNameChars, c) >= 0 ? '_' : c);
        }
        string cleaned = sb.ToString().Trim();
        return cleaned.Length > 50 ? cleaned[..50].Trim() : cleaned;
    }

    /// <summary>Sets the view's name to "Model Check_&lt;code&gt;_&lt;cleaned desc&gt;", retrying with a numeric suffix on a duplicate-name clash.</summary>
    private static string AssignUniqueViewName(View3D view, string code, string desc)
    {
        string baseName = $"Model Check_{code}_{CleanForViewName(desc)}";
        string name = baseName;
        for (int attempt = 2; attempt <= 50; attempt++)
        {
            try { view.Name = name; return name; }
            catch { name = $"{baseName} ({attempt})"; }
        }
        // Practically unreachable (50 real name clashes), but guarantees a name gets set.
        name = $"{baseName} ({Guid.NewGuid():N})";
        view.Name = name;
        return name;
    }

    // ---- master warning-index sidecar (append-only "Warning Description","Warning Index") ----

    private string GetOrAssignCode(string desc)
    {
        if (_descToCode.TryGetValue(desc, out string? existing))
            return existing;

        string code = $"W-{_nextNumber:D2}";
        _nextNumber++;
        _descToCode[desc] = code;
        AppendIndexRow(desc, code);
        return code;
    }

    // Invariant this reader depends on: descriptions are newline-normalized at capture (see
    // NormalizeDescription), so the sidecar is physically one line per row — no multi-line
    // CSV parsing needed here, a plain per-line split is safe.
    private void LoadIndex()
    {
        _descToCode.Clear();
        _nextNumber = 1;
        if (_sidecarPath == null || !File.Exists(_sidecarPath)) return;

        try
        {
            foreach (string line in File.ReadAllLines(_sidecarPath).Skip(1)) // skip the header row
            {
                List<string> fields = ParseCsvLine(line);
                if (fields.Count < 2) continue;
                string desc = fields[0];
                string code = fields[1];
                _descToCode[desc] = code;
                if (code.StartsWith("W-") && int.TryParse(code.Substring(2), out int n) && n >= _nextNumber)
                    _nextNumber = n + 1;
            }
        }
        catch
        {
            // Fail-soft: an unreadable/corrupt sidecar just means numbering restarts at 1.
        }
    }

    private void AppendIndexRow(string desc, string code)
    {
        if (_sidecarPath == null) return; // destination unreachable — in-memory numbering only

        try
        {
            bool newFile = !File.Exists(_sidecarPath) || new FileInfo(_sidecarPath).Length == 0;
            var sb = new StringBuilder();
            if (newFile)
                sb.AppendLine("Warning Description,Warning Index");
            sb.AppendLine($"{CsvFormat.Escape(desc)},{CsvFormat.Escape(code)}");
            File.AppendAllText(_sidecarPath, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Fail-soft, matching the other CSV loggers — never let sidecar I/O abort the batch.
        }
    }

    /// <summary>Minimal RFC4180-style reader for the two-column sidecar this action writes itself.</summary>
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
