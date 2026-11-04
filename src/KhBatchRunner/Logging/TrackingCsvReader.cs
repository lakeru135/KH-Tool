using System.IO;
using System.Text;

namespace KhBatchRunner.Logging;

/// <summary>
/// Reads back a tracking CSV written by <see cref="TrackingCsvLogger"/> and answers the one question
/// recovery depends on: <b>which models are finished?</b>
///
/// One row is written per (model, action) once that action has finished, so a model that is missing
/// rows is one whose Revit instance died, hung, or was killed before reaching them — exactly the set
/// worth rerunning. Two rules make the answer trustworthy:
///
/// <list type="bullet">
/// <item>an <b>ERROR</b> row is a result, not a gap: the model was opened and genuinely failed, and
/// rerunning it would just fail again;</item>
/// <item>some rows is not the same as all rows — a model needs one per action before it counts.
/// See <see cref="CompletedModels"/>, and the run that shipped seven models half-processed because
/// this file used to say otherwise.</item>
/// </list>
///
/// The file is read under <see cref="LogFileLock"/> because sibling group instances may be appending
/// to it at the same moment.
/// </summary>
internal static class TrackingCsvReader
{
    /// <summary>Status values used for run-level notes rather than a model's outcome
    /// (<see cref="RunNote"/>) — they carry no model name and must never count as a result.</summary>
    private static readonly HashSet<string> NoteStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "INFO", "SUMMARY" };

    /// <summary>
    /// The set of model names (the <c>Model</c> column) that are genuinely <b>finished</b> — every
    /// action in the run produced a row, or the model failed outright. Case-insensitive, because
    /// that column is a file name. Returns empty when the file does not exist yet — the correct
    /// answer for "nothing has finished".
    ///
    /// <para>This replaced an earlier "has at least one outcome row" test, which answered a weaker
    /// question than recovery needs. A model whose instance was killed after action 3 of 6 has rows,
    /// so it counted as done and was never retried: it shipped half-processed, with nothing in the
    /// log saying so. MEASURED 2026-07-26: seven models in a 98-model run, three of them killed
    /// mid-sequence by the supervisor's own ceiling bug.</para>
    ///
    /// <para>The action count comes from the Task cell, which <c>BatchEngine</c> writes as
    /// <c>"{i} of {n} — {action}"</c> whenever a run has more than one action, and as the bare
    /// action name when it has exactly one. So a row without that prefix means a single-action run,
    /// and one row completes the model.</para>
    ///
    /// <para>A model with any <b>non-OK</b> row counts as finished regardless. That is the existing
    /// "an ERROR is a result, not a gap" rule, and it is load-bearing here: an action that fails
    /// ends the model's sequence, so its indices can never reach <c>n</c>, and without this a failing
    /// model would be retried until it exhausted every round.</para>
    /// </summary>
    public static HashSet<string> CompletedModels(string trackingLogPath)
    {
        var complete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(trackingLogPath) || !File.Exists(trackingLogPath)) return complete;

        List<string[]> rows = ParseCsv(ReadAllShared(trackingLogPath));
        if (rows.Count == 0) return complete;

        string[] header = rows[0];
        int modelCol = IndexOf(header, "Model");
        int taskCol = IndexOf(header, "Task");
        int statusCol = IndexOf(header, "Status");
        if (modelCol < 0) return complete;

        // model -> the action indices that reported OK, and how many actions the run has.
        var seen = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < rows.Count; i++)
        {
            string[] r = rows[i];
            if (modelCol >= r.Length) continue;
            string model = r[modelCol].Trim();
            if (model.Length == 0) continue;                       // a run-level note

            // A row missing its Status cell was torn off mid-write — the file's last line when an
            // instance is killed at just the wrong moment. Treat it as NO EVIDENCE rather than as a
            // completed action: re-running a model that had in fact finished costs one redundant
            // open, while trusting a torn row drops that model from the batch for good, which is
            // the failure this whole file exists to prevent.
            if (statusCol < 0 || statusCol >= r.Length) continue;
            string status = r[statusCol].Trim();
            if (NoteStatuses.Contains(status)) continue;

            if (!status.Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                complete.Add(model);                               // a genuine failure is a result
                continue;
            }

            string task = taskCol >= 0 && taskCol < r.Length ? r[taskCol] : "";
            (int index, int total) = ParseTaskPosition(task);
            if (!seen.TryGetValue(model, out HashSet<int>? indices))
                seen[model] = indices = new HashSet<int>();
            indices.Add(index);
            // Keep the largest total seen: a run whose action list grew between rounds should be
            // judged against the longer list, not the shorter.
            totals[model] = Math.Max(totals.TryGetValue(model, out int t) ? t : 0, total);
        }

        // KeyValuePair deconstruction is .NET Core only — see the target-framework notes below.
        foreach (KeyValuePair<string, HashSet<int>> entry in seen)
            if (entry.Value.Count >= totals[entry.Key])
                complete.Add(entry.Key);

        return complete;
    }

    /// <summary>
    /// Pulls <c>(i, n)</c> out of a Task cell written as <c>"{i} of {n} — {action}"</c>. A cell
    /// with no such prefix is a single-action run, which is <c>(1, 1)</c>.
    /// </summary>
    private static (int Index, int Total) ParseTaskPosition(string task)
    {
        const string sep = " of ";
        if (string.IsNullOrEmpty(task)) return (1, 1);

        // Substring rather than AsSpan: the span overload of int.TryParse is .NET Core only, and
        // this assembly also targets .NET Framework 4.8 for Revit 2024/2025.
        int at = task.IndexOf(sep, StringComparison.Ordinal);
        if (at <= 0) return (1, 1);
        if (!int.TryParse(task.Substring(0, at), out int index)) return (1, 1);

        int rest = at + sep.Length;
        int end = rest;
        while (end < task.Length && char.IsDigit(task[end])) end++;
        if (end == rest || !int.TryParse(task.Substring(rest, end - rest), out int total)) return (1, 1);

        return (index >= 1 && total >= 1 && index <= total) ? (index, total) : (1, 1);
    }

    /// <summary>
    /// How the run actually went: outcome rows that say OK, and everything else. Run-level notes
    /// are excluded on the same rule as <see cref="CompletedModels"/> — they are commentary, not
    /// results. Used for the completion notification, so it can say "3 failed" rather than only
    /// that the run ended.
    /// </summary>
    public static (int Ok, int Failed) CountByStatus(string trackingLogPath)
    {
        int ok = 0, failed = 0;
        if (string.IsNullOrWhiteSpace(trackingLogPath) || !File.Exists(trackingLogPath))
            return (0, 0);

        List<string[]> rows = ParseCsv(ReadAllShared(trackingLogPath));
        if (rows.Count == 0) return (0, 0);

        string[] header = rows[0];
        int modelCol = IndexOf(header, "Model");
        int statusCol = IndexOf(header, "Status");
        if (modelCol < 0 || statusCol < 0) return (0, 0);

        for (int i = 1; i < rows.Count; i++)
        {
            string[] r = rows[i];
            if (modelCol >= r.Length || statusCol >= r.Length) continue;
            if (r[modelCol].Trim().Length == 0) continue;              // a run-level note

            string status = r[statusCol].Trim();
            if (NoteStatuses.Contains(status)) continue;
            if (status.Equals("OK", StringComparison.OrdinalIgnoreCase)) ok++;
            else failed++;
        }
        return (ok, failed);
    }

    /// <summary>Reads the whole file while sibling instances hold it open for append.</summary>
    private static string ReadAllShared(string path)
    {
        string text = "";
        LogFileLock.Write(path, () =>
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                text = sr.ReadToEnd();
            }
            catch { /* unreadable this tick — the caller treats that as "nothing" */ }
        });
        return text;
    }

    private static int IndexOf(string[] header, string name)
    {
        for (int i = 0; i < header.Length; i++)
            if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Minimal RFC-4180 reader matching what <see cref="CsvFormat.Escape"/> writes: fields may be
    /// quoted, quotes are doubled inside, and a quoted field may span newlines (an ERROR row's
    /// Message can contain one).
    /// </summary>
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        if (string.IsNullOrEmpty(text)) return rows;

        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(field.ToString()); field.Clear();
                    break;
                case '\r':
                    break;                                   // handled by the \n that follows
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row.ToArray()); row.Clear();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }
        // A final line with no trailing newline (or a file cut short mid-write by a killed instance).
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }
        return rows;
    }
}
