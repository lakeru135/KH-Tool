using KhBatchRunner.Schedule;

using System.Runtime.InteropServices;

namespace KhBatchLauncher;

/// <summary>
/// Removes toasts from Notification Center once they have outlived their usefulness —
/// <c>KhBatchLauncher.exe --toast-sweep</c>, and also the first thing <c>--toast</c> does.
///
/// WHY REMOVAL RATHER THAN AN EXPIRY. Windows will expire a toast for us if it is given an
/// <c>ExpirationTime</c>, and the PowerShell delivery path does exactly that. <see cref="ToastWinRt"/>
/// cannot: setting it needs an <c>IReference&lt;DateTime&gt;</c>, and the hand-built one was accepted
/// by Windows and then treated as ALREADY EXPIRED, dropping toasts within the second (see that
/// file's remarks). So on a locked-down PC, where the PowerShell path is refused and every
/// notification comes through here, nothing ever cleared. This does the clearing instead.
///
/// WHY THIS IS THE SAFE HALF OF THAT PROBLEM. The interop below takes nothing but strings — no
/// <c>IReference</c>, no structs, no value marshalling of any kind — and it runs AFTER a toast has
/// been shown, never on the path that shows one. The worst a bug in here can do is fail to remove
/// something, which is the behaviour being fixed. A bug in the expiry route silently destroys the
/// message instead, which is the exact failure the whole notification feature exists to end.
///
/// GROUPS ARE THE BLAST RADIUS, and that is deliberate. Expiring toasts are raised into
/// <see cref="ToastWinRt.TransientGroup"/> and failures into <see cref="ToastWinRt.PersistentGroup"/>.
/// Every call here names the transient group, so "a failure toast is never removed automatically" is
/// enforced by Windows rather than by this code getting its bookkeeping right — and even a mistake in
/// the vtable slots below cannot reach a failure, because no call made here is ever handed the group
/// a failure lives in.
/// </summary>
internal static class ToastHistory
{
    /// <summary>
    /// Removes every ledger entry whose time has come. Returns a one-line summary for the caller to
    /// record, or "" when there was nothing to do — a sweep that removed nothing should not add a
    /// line to the trail on every single notification.
    /// </summary>
    internal static string SweepExpired()
    {
        int removed = 0;
        int notFound = 0;
        var failures = new List<string>();

        int due = ToastLedger.Sweep(DateTimeOffset.UtcNow, entry =>
        {
            string? why = TryRemove(entry, out bool wasPresent);
            if (why != null) { if (failures.Count < 3) failures.Add(why); }  // a few, not one per row
            else if (wasPresent) removed++;
            else notFound++;
        });

        if (due == 0) return "";

        // "not found" is counted SEPARATELY from "removed" even though both are successes. They look
        // identical from the outside and mean opposite things about whether this code works: a sweep
        // reporting only not-founds is exactly what a wrong vtable slot produced during development,
        // while reporting them as removals made that bug invisible. If this line ever reads "0
        // removed, N not present" for toasts that are visibly still on screen, suspect the interop.
        string summary = $"toast sweep: {removed} removed, {notFound} not present, of {due} due";
        if (failures.Count > 0) summary += "; failed: " + string.Join(" | ", failures.ToArray());
        return summary;
    }

    /// <summary>
    /// Removes one toast. Returns null on success, or WHY it failed.
    ///
    /// The reason is carried rather than swallowed because this is hand-written ABI interop against
    /// declarations taken from documentation instead of from SDK metadata: the difference between
    /// "Windows no longer had that toast" (fine, the common case) and "the interface could not be
    /// obtained" (a real bug in the declarations below) is invisible from the outside, and a silent
    /// boolean is exactly how the 1.3.4 notification crash stayed unexplained for three releases.
    /// </summary>
    /// <param name="wasPresent">False when Windows had no such toast — a success, but a different
    /// one. See <see cref="SweepExpired"/> for why the two are never merged.</param>
    private static string? TryRemove(ToastLedgerEntry entry, out bool wasPresent)
    {
        wasPresent = false;
        try
        {
            ToastWinRt.InitializeRuntime();
            var statics = (IToastNotificationManagerStatics2)ToastWinRt.GetActivationFactory(
                "Windows.UI.Notifications.ToastNotificationManager",
                typeof(IToastNotificationManagerStatics2).GUID);

            // WithId, not the bare overload: this process has no AppUserModelID of its own, so the
            // identity has to be named explicitly — and it must be the one Windows ACCEPTED when the
            // toast was raised, which is why the ledger stores what the raiser reported rather than
            // what it asked for.
            statics.get_History().RemoveGroupedTagWithId(entry.Tag, entry.Group, entry.AppId);
            wasPresent = true;
            return null;
        }
        catch (Exception ex) when (ex.HResult == ErrorNotFound)
        {
            // MEASURED, not assumed: Windows answers a removal for a toast it does not have with
            // HRESULT_FROM_WIN32(ERROR_NOT_FOUND). That is the ORDINARY case — the user dismissed it
            // by hand, or Windows aged it out of history first — and the state we wanted is the state
            // we have. Counting it as a failure would make a healthy sweep report failures every day
            // and bury a real one.
            return null;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + " 0x" + ex.HResult.ToString("X8");
        }
    }

    /// <summary>HRESULT_FROM_WIN32(ERROR_NOT_FOUND) — "there is no such toast", which is success
    /// for a removal.</summary>
    private const int ErrorNotFound = unchecked((int)0x80070490);

    // The ABI declarations below follow the same rule as ToastWinRt's: every method in vtable order,
    // including the ones never called, because the runtime dispatches by slot and an omission
    // silently calls its neighbour. Written from the documented Windows.UI.Notifications ABI rather
    // than from a Windows SDK metadata reference, for the reason ToastWinRt explains at length —
    // the point of this component is to need nothing the locked-down PC can refuse.

    [ComImport, Guid("7AB93C52-0E48-4750-BA9D-1A4113981847"),
     InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IToastNotificationManagerStatics2
    {
        IToastNotificationHistory get_History();
    }

    [ComImport, Guid("5CADDC63-01D3-4C97-986F-0533483FEE14"),
     InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IToastNotificationHistory
    {
        void RemoveGroup([MarshalAs(UnmanagedType.HString)] string group);

        void RemoveGroupWithId(
            [MarshalAs(UnmanagedType.HString)] string group,
            [MarshalAs(UnmanagedType.HString)] string applicationId);

        // SLOT ORDER VERIFIED AGAINST THE LIVE INTERFACE, not assumed — and it is not the order the
        // names suggest. The three-argument WithId form comes BEFORE the two-argument one. Declared
        // the intuitive way round first, this called RemoveGroupedTag(tag, group), which resolves the
        // identity from the calling process's ambient AppUserModelID — this process has none, so
        // every removal answered ERROR_NOT_FOUND and every toast stayed on screen while the sweep
        // reported success. Enumerate Windows.UI.Notifications.IToastNotificationHistory's methods
        // before touching this.
        void RemoveGroupedTagWithId(
            [MarshalAs(UnmanagedType.HString)] string tag,
            [MarshalAs(UnmanagedType.HString)] string group,
            [MarshalAs(UnmanagedType.HString)] string applicationId);

        void RemoveGroupedTag(
            [MarshalAs(UnmanagedType.HString)] string tag,
            [MarshalAs(UnmanagedType.HString)] string group);

        void Remove([MarshalAs(UnmanagedType.HString)] string tag);

        void Clear();

        void ClearWithId([MarshalAs(UnmanagedType.HString)] string applicationId);
    }
}
