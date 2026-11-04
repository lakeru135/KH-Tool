using Autodesk.Revit.DB;

namespace KhBatchRunner.Batch;

/// <summary>
/// The Revit half of <see cref="WorksetAudit"/>: looks at a document whose worksets are OPEN and
/// records, per workset, whether everything in it was a link.
///
/// <para><b>Separate from <see cref="WorksetAudit"/> on purpose.</b> That class is the store — pure
/// file and JSON, testable from any machine. This one needs a live <see cref="Document"/> and a
/// collector, so it can only run inside Revit. Same split as <c>ReopenLadder</c> against
/// <c>BatchEngine</c>, and <c>InstanceBudget.CapFor</c> against <c>MaxConcurrent</c>.</para>
///
/// <para><b>Why the engine calls this and not an action.</b> It lived inside
/// <see cref="Actions.ExportNwcAction"/> until 1.4.3, guarded so that it only ran when that action's
/// own options set wanted no links. The guard made one action self-consistent — it produced a record
/// under exactly the condition it consumed one — and starved every other consumer. Batch Interop
/// opened all 65 worksets of a model on every run and recorded nothing, so when it came to ask
/// "which of these hold only links?" the answer was always "no idea".
/// MEASURED in bundle <c>BKKDDCJ74R3_20260819_1645</c>: 5,522 lines of surface.log, not one
/// <c>recorded …</c> line, and both logged plans opened every workset.</para>
///
/// <para><b>So the guard is gone and the price is paid unconditionally.</b> The pass costs one
/// collector sweep — MEASURED 2026-08-15 at 6–7 s per model, against a 38 s export. That was a real
/// fraction of an export and is a rounding error against what it buys: in the same bundle, one host
/// model loaded <b>19 linked files</b> and spent 38 minutes on the open that loaded them. Paying
/// 7 s on every run that opens worksets, to let the next one skip that, is not a close call.</para>
/// </summary>
internal static class WorksetContentsProbe
{
    /// <summary>
    /// Records what each OPEN user workset of <paramref name="doc"/> holds, against the model
    /// identity the engine published in <see cref="ModelContext"/>. Silent no-op when there is no
    /// identity to key the record by (a detached document cannot say which cloud model it is), when
    /// the document is not workshared, or when nothing is open.
    ///
    /// <para><b>Only OPEN worksets are judged.</b> A closed one holds no loaded elements, so it
    /// would look empty and be recorded as "link-only" — the exact false negative the whole
    /// mechanism exists to avoid, and the one that would leave real geometry shut on the next
    /// run.</para>
    ///
    /// <para><b>One collector pass</b>, not one per workset: elements are grouped by their own
    /// <see cref="Element.WorksetId"/> as they go by. A workset with no elements at all is left
    /// unrecorded — there is nothing to prove about it and nothing to gain by shutting it.</para>
    ///
    /// <para>Never throws. An optimisation's bookkeeping must not be able to fail a run.</para>
    /// </summary>
    public static void Record(Document doc)
    {
        try
        {
            string key = ModelContext.CurrentKey;
            if (string.IsNullOrEmpty(key) || !doc.IsWorkshared) return;

            var open = new Dictionary<WorksetId, string>();
            foreach (Workset w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                if (w.IsOpen) open[w.Id] = w.Name;
            if (open.Count == 0) return;

            var seen = new HashSet<WorksetId>();
            var hasNonLink = new HashSet<WorksetId>();
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                WorksetId id = e.WorksetId;
                if (!open.ContainsKey(id)) continue;
                seen.Add(id);
                if (e is not RevitLinkInstance) hasNonLink.Add(id);
            }

            var findings = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (WorksetId id in seen) findings[open[id]] = !hasNonLink.Contains(id);
            WorksetAudit.Record(key, findings);

            // Traced even when the answer is ZERO. Until 1.4.3 this spoke only when it had found
            // something, so silence meant both "ran, found nothing" and "never ran" — and that
            // ambiguity is why the bundle above could not say which of the two had happened. A
            // record that only reports good news cannot be debugged.
            int linkOnly = findings.Count(f => f.Value);
            Diagnostics.RunTrace.Mark(
                $"{doc.Title} | recorded {linkOnly} link-only workset(s) of {findings.Count} inspected");
        }
        catch (Exception ex)
        {
            Diagnostics.RunTrace.Mark("workset contents could not be inspected: " + ex.Message);
        }
    }
}
