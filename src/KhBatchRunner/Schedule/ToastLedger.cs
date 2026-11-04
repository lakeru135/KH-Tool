using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KhBatchRunner.Schedule;

/// <summary>One recorded toast: what to remove, where, and when it stops being worth showing.</summary>
internal sealed class ToastLedgerEntry
{
    public ToastLedgerEntry(DateTimeOffset expiresAt, string appId, string group, string tag)
    {
        ExpiresAt = expiresAt;
        AppId = appId;
        Group = group;
        Tag = tag;
    }

    /// <summary>When this toast may be removed from Notification Center. Stored and compared in
    /// UTC — a scheduled run can easily straddle a DST change, and the two sides of that comparison
    /// are written by different processes at different times.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>The AppUserModelID Windows ACTUALLY accepted for this toast, not the one that was
    /// requested. Removal has to name the same identity that raised it, and the raiser tries a list
    /// of candidates (<c>RunNotification.ToastAppIdCandidates</c>).</summary>
    public string AppId { get; }

    public string Group { get; }
    public string Tag { get; }
}

/// <summary>
/// The record of toasts that are allowed to disappear on their own, and when.
///
/// WHY THIS EXISTS. Retention was already designed — <c>RunNotification.ExpiryFor</c> gives progress
/// news an hour and a failure none — but it only ever worked on the PowerShell delivery path, which
/// hands Windows an <c>ExpirationTime</c> and lets Windows do the removing. On a locked-down PC that
/// path is refused and delivery falls to <see cref="KhBatchLauncher"/>'s direct WinRT path, which
/// deliberately sets no expiration (a hand-built <c>IReference&lt;DateTime&gt;</c> was accepted and
/// then treated as ALREADY EXPIRED, dropping toasts within the second). So on exactly the machine
/// that needed notifications most, nothing ever cleared: success and per-attempt toasts were still
/// on screen nine hours later.
///
/// Since Windows will not expire them for us there, something has to remove them afterwards — and
/// removal needs the tag/group/identity of a toast raised by a process that has long since exited.
/// That is what this file keeps.
///
/// A FAILURE TOAST IS NEVER RECORDED HERE, and that is the entire mechanism for "failures wait to be
/// dismissed". It needs no flag of its own and no special case in the sweep: <c>ExpiryFor</c>
/// returns null for a failure, the raiser sends an empty expiry, no row is written, and a toast with
/// no row can never be selected for removal. The policy lives in one place and is enforced by
/// absence.
///
/// Location matches <see cref="Diagnostics.RunTrace"/>'s and for the same measured reason: Revit
/// gives every process its own GUID-named %TEMP%, so a ledger under <c>Path.GetTempPath()</c> would
/// be private to one instance — and a parallel run has several instances raising toasts at once.
/// </summary>
internal static class ToastLedger
{
    private const string FileName = "toast-ledger.txt";

    /// <summary>Tab-separated: tabs cannot appear in an ISO timestamp, an AppUserModelID, or the
    /// hex tag the raiser generates, so no escaping is needed and a malformed line stays malformed
    /// rather than becoming a plausible wrong one.</summary>
    private const char Separator = '\t';

    private const int FieldCount = 4;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KhBatchRunner", FileName);

    /// <summary>
    /// Appends one entry. Best-effort and never throws — failing to record a toast means it lingers,
    /// which is the behaviour being fixed, not a new failure worth aborting a run for.
    /// </summary>
    public static void Record(ToastLedgerEntry entry, string? path = null)
    {
        string target = path ?? DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Several Revit instances raise toasts during a parallel run; the same cross-process
            // lock the CSV loggers use keeps their appends from interleaving.
            Logging.LogFileLock.Write(target, () =>
                File.AppendAllText(target, Format(entry) + Environment.NewLine, Encoding.UTF8));
        }
        catch { /* a lingering toast is not worth an exception */ }
    }

    /// <summary>
    /// Reads the ledger, hands every DUE entry to <paramref name="remove"/>, and rewrites the file
    /// with the rest. The whole read-modify-write runs under the cross-process lock so a toast
    /// raised by another instance mid-sweep cannot be lost from the file.
    ///
    /// An entry is dropped from the ledger once it has been offered to <paramref name="remove"/>,
    /// whatever that returns. A removal can fail because the user already dismissed the toast, or
    /// because Windows no longer has it — neither is a reason to keep asking forever, and a ledger
    /// that only grows would eventually be the problem instead of the fix.
    /// </summary>
    /// <returns>How many entries were due (i.e. offered for removal).</returns>
    public static int Sweep(DateTimeOffset nowUtc, Action<ToastLedgerEntry> remove, string? path = null)
    {
        string target = path ?? DefaultPath;
        int due = 0;
        try
        {
            if (!File.Exists(target)) return 0;
            Logging.LogFileLock.Write(target, () =>
            {
                string[] lines = File.ReadAllLines(target, Encoding.UTF8);
                var keep = new List<string>(lines.Length);
                foreach (string line in lines)
                {
                    ToastLedgerEntry? entry = Parse(line);
                    // A line we cannot read is dropped rather than kept: it names no toast we could
                    // ever remove, so keeping it would only make the file grow forever.
                    if (entry == null) continue;
                    if (entry.ExpiresAt > nowUtc) { keep.Add(line); continue; }

                    due++;
                    try { remove(entry); } catch { /* see summary: offered once, then forgotten */ }
                }

                if (keep.Count == 0) File.Delete(target);
                else File.WriteAllLines(target, keep, Encoding.UTF8);
            });
        }
        catch { /* sweeping is tidying; it must never be the thing that breaks a run */ }
        return due;
    }

    /// <summary>Serialised form. Kept next to <see cref="Parse"/> so the two cannot drift.</summary>
    internal static string Format(ToastLedgerEntry entry) => string.Join(
        Separator.ToString(),
        new[]
        {
            entry.ExpiresAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            entry.AppId,
            entry.Group,
            entry.Tag
        });

    /// <summary>
    /// Null for anything unreadable — a blank line, a truncated write, a half-line from a crash
    /// mid-append, or a future format with more fields. The caller drops those; nothing here throws,
    /// because this parses a file that several processes append to and that no one validates on the
    /// way in.
    /// </summary>
    internal static ToastLedgerEntry? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        string[] parts = line!.Split(Separator);
        if (parts.Length != FieldCount) return null;

        if (!DateTimeOffset.TryParse(
                parts[0], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset expires))
            return null;

        // A row that names no toast cannot be acted on. Group may legitimately be empty on a
        // machine too old for IToastNotification2, where the raiser could not set one.
        if (parts[1].Length == 0 || parts[3].Length == 0) return null;

        return new ToastLedgerEntry(expires.ToUniversalTime(), parts[1], parts[2], parts[3]);
    }
}
