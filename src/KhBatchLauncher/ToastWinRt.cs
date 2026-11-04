using KhBatchRunner.Schedule;

using System.Runtime.InteropServices;
using System.Text;

namespace KhBatchLauncher;

/// <summary>
/// A REAL Notification Center toast, raised by calling Windows directly — no PowerShell anywhere in
/// the path. <c>KhBatchLauncher.exe --toast</c>.
///
/// WHY THIS EXISTS. The add-in has always preferred a real Windows toast, and got one by shelling
/// out to Windows PowerShell to reach the WinRT notification API — one implementation that served
/// both the net48 (Revit 2024) and net8 (2025/2026) builds without shipping another runtime. That
/// convenience is what broke on a managed PC: policy puts Windows PowerShell in ConstrainedLanguage
/// mode, where it may not call .NET or WinRT types at all, so the preferred path died silently and
/// every notification fell through to the drawn card (see <see cref="NotifyCard"/>).
///
/// The correction is that CONSTRAINEDLANGUAGE IS A POWERSHELL RESTRICTION, NOT A WINDOWS ONE. It
/// limits what a *script* may do; it says nothing about compiled code. This executable already runs
/// on that locked-down PC — it is what draws the fallback card there today — so calling the same
/// WinRT API from here is subject to none of it. The lockdown never blocked notifications; it
/// blocked the interpreter we happened to route them through.
///
/// WHY THE INTEROP IS HAND-WRITTEN. Reaching WinRT from .NET Framework 4.8 normally means a
/// Windows SDK metadata reference (Windows.winmd) or the Windows App SDK. Both are build-machine
/// and target-machine dependencies, and the App SDK additionally wants MSIX packaging — a large
/// price for four API calls, on a component whose whole point is to need nothing the locked-down PC
/// can refuse. .NET Framework can talk to WinRT with no such help through
/// <see cref="ComInterfaceType.InterfaceIsIInspectable"/>: the runtime supplies the IInspectable
/// vtable prefix, so these declarations are the entire dependency. WinRT itself ships in Windows.
///
/// KEEPING THE LAUNCHER AS THE ONE HOST is deliberate, and for the same reason PowerShell was
/// chosen originally: one implementation serves every Revit year and both editions. The add-in
/// still decides WHAT to say; this only decides how it reaches the screen.
///
/// EXPIRY NOW WORKS HERE TOO, and what was wrong for three releases is worth stating exactly.
/// 1.3.5.3 recorded that <c>ExpirationTime</c> could not be set: the hand-built
/// <c>IReference&lt;DateTime&gt;</c> was accepted by Windows and then treated as ALREADY EXPIRED, so
/// the toast left Notification Center within the second. The value was never the problem — the same
/// instant encoded identically to the PowerShell projection's (134306883278187761). Neither was the
/// vtable slot. <b>The problem was the return-value marshalling:</b> declared as a marshalled
/// <c>object</c>, .NET's WinRT projection recognises <c>IReference&lt;DateTime&gt;</c> and converts
/// it into a managed <c>System.DateTimeOffset</c>, so what came back had no COM identity left and
/// what reached <c>put_ExpirationTime</c> was not an IReference at all. Declared as a raw pointer it
/// survives — its own GetRuntimeClassName says
/// <c>Windows.Foundation.IReference`1&lt;Windows.Foundation.DateTime&gt;</c>. See
/// <see cref="TrySetExpiration"/>.
///
/// WHY THAT MATTERED ENOUGH TO GO BACK FOR. The first attempt at a fix removed expired toasts
/// afterwards instead (<see cref="ToastHistory"/>), which cannot work for the case that actually
/// produces them: a scheduled run's Revit EXITS when it finishes, so at the moment the hour is up
/// there is nothing of ours alive to do the removing. Measured on the office PC — three toasts from
/// a 20:33-20:38 run were all still there at 21:38 with Revit closed. Only Windows can expire a
/// notification on a machine where nothing of ours is running.
///
/// The removal path is KEPT as the fallback: a toast whose expiry could not be set is raised into
/// <see cref="TransientGroup"/> and recorded in <see cref="ToastLedger"/> so it can still be tidied
/// later, and toasts left behind by earlier builds are cleared the same way. A failure gets no
/// expiry at all, is raised into <see cref="PersistentGroup"/>, recorded nowhere, and waits to be
/// dismissed exactly as before.
/// </summary>
internal static class ToastWinRt
{
    /// <summary>
    /// Group for a toast that must wait to be dismissed — a run that FAILED. Matches the group the
    /// PowerShell path uses, so a failure raised by either mechanism is the same item to Windows.
    ///
    /// Nothing in <see cref="ToastHistory"/> ever names this group, which is what makes "failures
    /// are never removed automatically" a property Windows enforces rather than a rule this code has
    /// to remember.
    /// </summary>
    internal const string PersistentGroup = "KhBatch";

    /// <summary>
    /// Group for a toast that may be tidied away later — progress news: an attempt starting, a run
    /// finishing cleanly. Separate from <see cref="PersistentGroup"/> so that removal calls can only
    /// ever be aimed at toasts that were always going to be removed.
    /// </summary>
    internal const string TransientGroup = "KhBatch.Transient";

    internal static int Run()
    {
        string xml = DecodeXml(Env(ScheduleEnvironment.NotifyToastXml));
        if (xml.Length == 0) return Program.ExitBadArguments;

        string appId = Env(ScheduleEnvironment.NotifyToastAppId);
        if (appId.Length == 0) return Program.ExitBadArguments;

        string tag = Env(ScheduleEnvironment.NotifyToastTag);
        string expiryRaw = Env(ScheduleEnvironment.NotifyToastExpiry);

        // Tidy before adding: every notification clears the ones whose time is up, so nothing has to
        // be scheduled and no extra process ever runs. A run that raises no further toast is caught
        // by the --toast-sweep at the next Revit startup instead.
        string swept = ToastHistory.SweepExpired();

        try
        {
            Show(appId, xml, tag, expiryRaw);

            // The identity that carried it, on stdout — the caller records this, and "which
            // identity did Windows accept" is the first question when a toast does not appear.
            Console.Out.Write(appId);
            if (swept.Length > 0) Console.Error.Write(swept);
            return 0;
        }
        catch (Exception ex)
        {
            // stderr rather than a log file: the caller already redirects and records both streams,
            // and a notification helper that writes its own log would be one more thing to find.
            // The whole exception, not just its Message: every failure in here is an interop
            // failure, where the type and the frame say which of a dozen ABI calls went wrong and
            // the message alone ("Object reference not set...") says nothing at all.
            Console.Error.Write(ex.ToString());
            return Program.ExitCouldNotStart;
        }
    }

    private static void Show(string appId, string xml, string tag, string expiryRaw)
    {
        InitializeRuntime();

        // An expiry means "this is progress news, it may be tidied away later"; its absence means a
        // failure, which waits to be dismissed. The distinction is decided once, upstream, by
        // RunNotification.ExpiryFor — this only reads the consequence.
        DateTimeOffset? expiry = ParseExpiry(expiryRaw);
        string group = expiry.HasValue ? TransientGroup : PersistentGroup;

        var document = (IXmlDocumentIO)ActivateInstance("Windows.Data.Xml.Dom.XmlDocument");
        document.LoadXml(xml);

        var notificationFactory = (IToastNotificationFactory)GetActivationFactory(
            "Windows.UI.Notifications.ToastNotification", typeof(IToastNotificationFactory).GUID);
        IToastNotification notification = notificationFactory.CreateToastNotification(document);

        // Tag and Group live on the second interface revision; a machine old enough to lack it can
        // still show the toast, it just cannot be addressed again afterwards — so on such a machine
        // an expiring toast is not recorded, because nothing could ever remove it.
        bool addressable = false;
        if (tag.Length > 0 && notification is IToastNotification2 taggable)
        {
            taggable.put_Tag(tag);
            taggable.put_Group(group);
            addressable = true;
        }

        // Hand Windows the expiry so it removes the toast with nothing of ours running — the only
        // thing that works for a scheduled run, because that Revit exits when it finishes.
        bool windowsWillExpire = expiry.HasValue && TrySetExpiration(notification, expiry.Value);

        var managerStatics = (IToastNotificationManagerStatics)GetActivationFactory(
            "Windows.UI.Notifications.ToastNotificationManager",
            typeof(IToastNotificationManagerStatics).GUID);
        managerStatics.CreateToastNotifierWithId(appId).Show(notification);

        // The ledger is now the FALLBACK, not the mechanism: it records only the toasts Windows was
        // not given an expiry for, so ToastHistory can still tidy them when something of ours next
        // runs. Recorded after Show, because a toast that was never raised must not leave a row
        // claiming it was.
        if (expiry.HasValue && addressable && !windowsWillExpire)
            ToastLedger.Record(new ToastLedgerEntry(expiry.Value, appId, group, tag));
    }

    /// <summary>
    /// Sets <c>ExpirationTime</c>, so Windows removes the toast on its own.
    ///
    /// THIS IS THE INTEROP 1.3.5.3 GAVE UP ON. It took TWO fixes, and neither was the vtable slot or
    /// the value — both of which were right the whole time:
    ///
    /// 1. <b>Do not let .NET project the return value.</b> Declared as a marshalled <c>object</c>,
    ///    the WinRT projection recognises <c>IReference&lt;DateTime&gt;</c> and converts it to a
    ///    managed <c>System.DateTimeOffset</c> — a value type with no COM identity left to pass on.
    ///    Every declaration in <see cref="IPropertyValueStatics"/> is therefore <c>PreserveSig</c>
    ///    with a raw <c>out IntPtr</c>.
    /// 2. <b>QueryInterface before handing the pointer over</b> — see the comment at the call.
    ///    This is what actually made the toast survive.
    ///
    /// Returns false rather than throwing if anything is off, and the caller then falls back to the
    /// ledger. A toast that is kept too long is a nuisance; a toast destroyed on delivery is the
    /// failure this whole feature exists to prevent, so no expiry is ever set on a doubtful value.
    /// </summary>
    private static bool TrySetExpiration(IToastNotification notification, DateTimeOffset expiry)
    {
        // Windows.Foundation.DateTime counts 100ns intervals from 1601-01-01 UTC — the FILETIME
        // epoch, which is exactly what ToFileTimeUtc produces. A time already past would ask Windows
        // to drop the toast on arrival, so it is refused outright rather than encoded.
        if (expiry <= DateTimeOffset.UtcNow) return false;

        long ticks;
        try { ticks = expiry.UtcDateTime.ToFileTimeUtc(); }
        catch { return false; }          // outside FILETIME's range
        if (ticks <= 0) return false;

        IntPtr boxed = IntPtr.Zero;      // what CreateDateTime returns: an IInspectable
        IntPtr asReference = IntPtr.Zero; // the same object as IReference<DateTime>
        try
        {
            var propertyValue = (IPropertyValueStatics)GetActivationFactory(
                "Windows.Foundation.PropertyValue", typeof(IPropertyValueStatics).GUID);
            if (propertyValue.CreateDateTime(ticks, out boxed) < 0 || boxed == IntPtr.Zero)
                return false;

            // THE STEP THAT WAS MISSING FOR THREE RELEASES. CreateDateTime hands back the object's
            // IInspectable — which for a property value is its IPropertyValue vtable — and Windows
            // does NOT QueryInterface what it is given: it treats the pointer as an
            // IReference<DateTime> and calls vtable slot 6 straight away. On IPropertyValue slot 6 is
            // get_Type, which for a DateTime returns PropertyType.DateTime = 14. So Windows read an
            // expiry of 14 ticks past 1601-01-01, i.e. ALREADY EXPIRED, and dropped the toast within
            // the second — the exact reported symptom, from a reference whose stored value was
            // perfectly correct all along. QueryInterface for the right interface and pass THAT.
            Guid iid = IReferenceOfDateTime;
            if (Marshal.QueryInterface(boxed, ref iid, out asReference) < 0 || asReference == IntPtr.Zero)
                return false;   // doubles as the validation guard: no QI, no expiry, keep the toast

            notification.put_ExpirationTime(asReference);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            // The notification AddRefs whatever it keeps; these release only our own references.
            if (asReference != IntPtr.Zero) Marshal.Release(asReference);
            if (boxed != IntPtr.Zero) Marshal.Release(boxed);
        }
    }

    /// <summary>
    /// IID of <c>Windows.Foundation.IReference`1&lt;Windows.Foundation.DateTime&gt;</c> — a
    /// parameterised-interface GUID, so it is a fixed value for this one instantiation rather than
    /// something derivable. Verified on this machine: <c>QueryInterface</c> for it succeeds on what
    /// <c>CreateDateTime</c> returns, and slot 6 of the result then reads back the exact instant
    /// that went in.
    /// </summary>
    private static readonly Guid IReferenceOfDateTime =
        new Guid("5541D8A7-497C-5AA4-86FC-7713ADBF2A2C");

    /// <summary>
    /// Parses the ISO-8601 instant the add-in passes in <c>KHBATCH_TOAST_EXPIRY</c>. Empty means a
    /// failure toast (<c>RunNotification.ExpiryFor</c> returns null for those). Unparseable is
    /// treated the same as empty: keeping a toast is the harmless outcome, removing one on a
    /// misread time is not.
    /// </summary>
    private static DateTimeOffset? ParseExpiry(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(
            raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset value)
            ? value.ToUniversalTime()
            : (DateTimeOffset?)null;
    }

    /// <summary>Initialises WinRT on this thread. S_FALSE / RPC_E_CHANGED_MODE both mean "already
    /// initialised", which is the normal case under <c>[STAThread] Main</c> and is not a failure.
    /// Safe to call more than once, which the sweep relies on.</summary>
    internal static void InitializeRuntime()
    {
        int hr = RoInitialize(RoInitType.SingleThreaded);
        if (hr < 0 && hr != RpcEChangedMode) Marshal.ThrowExceptionForHR(hr);
    }

    private static object ActivateInstance(string classId)
    {
        int hr = RoActivateInstance(classId, out object instance);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return instance;
    }

    internal static object GetActivationFactory(string classId, Guid iid)
    {
        int hr = RoGetActivationFactory(classId, ref iid, out object factory);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return factory;
    }

    private static string DecodeXml(string base64)
    {
        if (base64.Length == 0) return "";
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(base64)); }
        catch (FormatException) { return ""; }
    }

    private static string Env(string name)
    {
        try { return Environment.GetEnvironmentVariable(name) ?? ""; }
        catch { return ""; }
    }

    private const int RpcEChangedMode = unchecked((int)0x80010106);

    private enum RoInitType
    {
        SingleThreaded = 0,
        MultiThreaded = 1
    }

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoInitialize(RoInitType initType);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int RoActivateInstance(
        [MarshalAs(UnmanagedType.HString)] string activatableClassId,
        [MarshalAs(UnmanagedType.IInspectable)] out object instance);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int RoGetActivationFactory(
        [MarshalAs(UnmanagedType.HString)] string activatableClassId,
        [In] ref Guid iid,
        [MarshalAs(UnmanagedType.IInspectable)] out object factory);

    // The interface declarations below are ABI contracts: every method must appear, in vtable
    // order, even the ones never called here — the runtime finds a method by its slot, so an
    // omission silently calls the wrong function. Members that are not needed are declared and left
    // unimplemented rather than removed.

    [ComImport, Guid("6CD0E74E-EE65-4489-9EBF-CA43E87BA637"),
     InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IXmlDocumentIO
    {
        void LoadXml([MarshalAs(UnmanagedType.HString)] string xml);
        void LoadXmlWithSettings([MarshalAs(UnmanagedType.HString)] string xml, IntPtr loadSettings);
        IntPtr SaveToFileAsync(IntPtr file);
    }

    [ComImport, Guid("04124B20-82C6-4229-B109-FD9ED4662B53"),
     InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IToastNotificationFactory
    {
        IToastNotification CreateToastNotification(IXmlDocumentIO content);
    }

    [ComImport, Guid("997E2675-059E-4E60-8B06-1760917C8B80"),
     InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IToastNotification
    {
        [return: MarshalAs(UnmanagedType.IInspectable)] object get_Content();

        void put_ExpirationTime(IntPtr value);

        IntPtr get_ExpirationTime();

        // Event add/remove pairs. Never subscribed here — a toast raised by this process is
        // activated through the protocol handler in its XML, not through a callback, because the
        // process is gone long before anyone clicks.
        IntPtr add_Dismissed(IntPtr handler);
        void remove_Dismissed(IntPtr token);
        IntPtr add_Activated(IntPtr handler);
        void remove_Activated(IntPtr token);
        IntPtr add_Failed(IntPtr handler);
        void remove_Failed(IntPtr token);
    }

    [ComImport, Guid("9DFB9FD1-143A-490E-90BF-B9FBA7132DE7"),
     InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IToastNotification2
    {
        void put_Tag([MarshalAs(UnmanagedType.HString)] string value);
        [return: MarshalAs(UnmanagedType.HString)] string get_Tag();
        void put_Group([MarshalAs(UnmanagedType.HString)] string value);
        [return: MarshalAs(UnmanagedType.HString)] string get_Group();
        void put_SuppressPopup([MarshalAs(UnmanagedType.U1)] bool value);
        [return: MarshalAs(UnmanagedType.U1)] bool get_SuppressPopup();
    }

    [ComImport, Guid("75927B93-03F3-41EC-91D3-6E5BAC1B38E7"),
     InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IToastNotifier
    {
        void Show(IToastNotification notification);
        void Hide(IToastNotification notification);
        int get_Setting();
        void AddToSchedule(IntPtr scheduledToast);
        void RemoveFromSchedule(IntPtr scheduledToast);
        IntPtr GetScheduledToastNotifications();
    }

    [ComImport, Guid("50AC103F-D235-4598-BBEF-98FE4D1A3AD4"),
     InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IToastNotificationManagerStatics
    {
        IToastNotifier CreateToastNotifier();
        IToastNotifier CreateToastNotifierWithId([MarshalAs(UnmanagedType.HString)] string applicationId);
        IXmlDocumentIO GetTemplateContent(int type);
    }

    /// <summary>
    /// Windows.Foundation.IPropertyValueStatics — the factory that boxes a value into the
    /// <c>IReference&lt;T&gt;</c> that <c>put_ExpirationTime</c> requires.
    ///
    /// EVERY METHOD IS PreserveSig WITH A RAW out IntPtr, and that is the entire fix over the 1.3.5.3
    /// attempt. Declared the ordinary way — <c>[return: MarshalAs(UnmanagedType.IInspectable)]
    /// object</c> — .NET's WinRT projection recognises <c>IReference&lt;DateTime&gt;</c> and helpfully
    /// converts it to a managed <c>System.DateTimeOffset</c>, and the COM object is gone before the
    /// caller ever sees it. Keeping it an opaque pointer is what preserves it.
    ///
    /// All seventeen entries are declared, in vtable order, because the runtime dispatches by slot —
    /// see the note above <see cref="IXmlDocumentIO"/>. <c>CreateDateTime</c> is the sixteenth.
    /// </summary>
    [ComImport, Guid("629BDBC8-D932-4FF4-96B9-8D96C5C1E858"),
     InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IPropertyValueStatics
    {
        [PreserveSig] int CreateEmpty(out IntPtr value);
        [PreserveSig] int CreateUInt8(byte value, out IntPtr result);
        [PreserveSig] int CreateInt16(short value, out IntPtr result);
        [PreserveSig] int CreateUInt16(ushort value, out IntPtr result);
        [PreserveSig] int CreateInt32(int value, out IntPtr result);
        [PreserveSig] int CreateUInt32(uint value, out IntPtr result);
        [PreserveSig] int CreateInt64(long value, out IntPtr result);
        [PreserveSig] int CreateUInt64(ulong value, out IntPtr result);
        [PreserveSig] int CreateSingle(float value, out IntPtr result);
        [PreserveSig] int CreateDouble(double value, out IntPtr result);
        [PreserveSig] int CreateChar16(char value, out IntPtr result);
        [PreserveSig] int CreateBoolean([MarshalAs(UnmanagedType.U1)] bool value, out IntPtr result);
        [PreserveSig] int CreateString([MarshalAs(UnmanagedType.HString)] string value, out IntPtr result);
        [PreserveSig] int CreateInspectable(IntPtr value, out IntPtr result);
        [PreserveSig] int CreateGuid(Guid value, out IntPtr result);
        [PreserveSig] int CreateDateTime(long value, out IntPtr result);
        [PreserveSig] int CreateTimeSpan(long value, out IntPtr result);
    }
}
