using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Actions;

/// <summary>
/// Multi Model Check: compare every <see cref="TextNote"/> against the model's Type Mark values
/// (<see cref="BuiltInParameter.ALL_MODEL_TYPE_MARK"/> on element types) — the batch port of the
/// team's "Check_00_Type Mark by Text Note" QA graph. Drafters sometimes annotate marks as plain
/// text instead of tags; those notes go stale when types change. One detail row per text note:
/// its view, cleaned text, whether any line of it matches a Type Mark, and which type(s) carry
/// that mark. The summary also counts DUPLICATE Type Marks (one mark on several types).
/// Read-only — no transaction (the Dynamo original's optional rolled-back-transaction PDF branch
/// is deliberately not ported, per the porting decisions).
///
/// NOTE: the matching rule (trimmed, whitespace-collapsed, case-insensitive, per-line) is the
/// conventional interpretation — the office .dyn wasn't on disk at port time. Confirm against
/// the real graph when available and tune here.
/// </summary>
public sealed class TypeMarkTextNoteAction : BatchActionBase
{
    public override string Name => "Type Mark by Text Note";

    public override WorksetPosture Worksets => WorksetPosture.ReadsNoInstances;

    public override string WorksetPostureReason =>
        "Collect() reads ElementType (types stay loaded whatever the worksets do) and TextNote. " +
        "Text notes are view-specific and belong to their VIEW's workset, and CloseAllWorksets " +
        "closes only USER worksets — so both halves of the comparison are complete. Audited " +
        "2026-08-16 and CLEARED; it was on the suspect list before anyone read the collectors.";

    public override IReadOnlyList<string> Columns { get; } =
        new[] { "View", "Text Note", "Matches Type Mark", "Type Mark", "Matched Types" };

    private string _perModelNote = "";
    public override string PerModelNote => _perModelNote;

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        Scan scan = Collect(doc);
        return new Dictionary<string, string>
        {
            ["Text Notes"] = scan.Notes.Count.ToString(),
            ["Matched"] = scan.Notes.Count(n => n.Marks.Count > 0).ToString(),
            ["Unmatched"] = scan.Notes.Count(n => n.Marks.Count == 0).ToString(),
            ["Type Marks"] = scan.MarkTypes.Count.ToString(),
            ["Duplicate Type Marks"] = scan.DuplicateMarks.ToString(),
        };
    }

    public override IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(UIApplication uiApp, Document doc)
    {
        Scan scan = Collect(doc);

        int unmatched = scan.Notes.Count(n => n.Marks.Count == 0);
        var noteParts = new List<string>();
        if (scan.Notes.Count == 0) noteParts.Add("no text notes");
        else if (unmatched > 0) noteParts.Add($"{unmatched} text note(s) match no Type Mark");
        if (scan.DuplicateMarks > 0) noteParts.Add($"{scan.DuplicateMarks} Type Mark(s) used by more than one type");
        _perModelNote = string.Join("; ", noteParts);

        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (NoteRow n in scan.Notes)
        {
            // A note may match several marks (one per line); join both the marks and the types
            // that carry them so the row is self-explanatory without opening the model.
            string types = string.Join(" | ", n.Marks
                .SelectMany(m => scan.MarkTypes.TryGetValue(m, out List<string>? t) ? t : new List<string>())
                .Distinct());
            rows.Add(new Dictionary<string, string>
            {
                ["View"] = n.View,
                ["Text Note"] = n.Text,
                ["Matches Type Mark"] = n.Marks.Count > 0 ? "Yes" : "No",
                ["Type Mark"] = string.Join(" | ", n.Marks),
                ["Matched Types"] = types,
            });
        }
        return rows;
    }

    private sealed class NoteRow
    {
        public string View = "";
        public string Text = "";
        public List<string> Marks = new();
    }

    private sealed class Scan
    {
        public List<NoteRow> Notes = new();
        // Type Mark -> "Category: Type Name" list of every type carrying it (case-insensitive key).
        public Dictionary<string, List<string>> MarkTypes = new(StringComparer.OrdinalIgnoreCase);
        public int DuplicateMarks;
    }

    private static Scan Collect(Document doc)
    {
        var scan = new Scan();

        // 1) Every element type's Type Mark (resolved via the BuiltInParameter, never the display
        //    name — parameter names move between Revit versions/languages).
        foreach (ElementType type in new FilteredElementCollector(doc)
                     .OfClass(typeof(ElementType)).Cast<ElementType>())
        {
            string mark;
            try { mark = Clean(type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString()); }
            catch { continue; }
            if (mark.Length == 0) continue;

            if (!scan.MarkTypes.TryGetValue(mark, out List<string>? list))
                scan.MarkTypes[mark] = list = new List<string>();
            string label = $"{type.Category?.Name ?? "(no category)"}: {type.Name}";
            if (!list.Contains(label)) list.Add(label);
        }
        scan.DuplicateMarks = scan.MarkTypes.Count(kv => kv.Value.Count > 1);

        // 2) Every text note, matched per LINE against the mark set — office notes often stack
        //    several marks in one note, and prose notes simply match nothing.
        foreach (TextNote note in new FilteredElementCollector(doc)
                     .OfClass(typeof(TextNote)).WhereElementIsNotElementType().Cast<TextNote>())
        {
            var row = new NoteRow();
            try
            {
                string raw = note.Text ?? "";
                row.Text = Clean(raw);
                if (row.Text.Length == 0) continue;

                foreach (string line in raw.Split('\n', '\r'))
                {
                    string candidate = Clean(line);
                    if (candidate.Length == 0) continue;
                    if (scan.MarkTypes.ContainsKey(candidate) && !row.Marks.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                        row.Marks.Add(candidate);
                }

                try { row.View = doc.GetElement(note.OwnerViewId)?.Name ?? ""; }
                catch { /* view unreadable — leave blank */ }
            }
            catch { continue; } // one unreadable note must not sink the model
            scan.Notes.Add(row);
        }

        return scan;
    }

    /// <summary>Trim and collapse all internal whitespace runs (incl. newlines) to single spaces.</summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return string.Join(" ", value!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
