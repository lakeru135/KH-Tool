using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using KhBatchRunner.Drw.Dialogs;

namespace KhBatchRunner.Drw.DoorWindow;

/// <summary>
/// The Enlarge Door &amp; Window window kept open beside Revit (the user, 2026-10-21: edit the set's
/// schedule while the window is up, and have the window follow). One window per session. Its
/// requests run through ONE external event (the AI Bridge's pattern, <c>Mcp/McpBridge.cs</c>):
/// a button press queues work, Revit runs it when it is free, and the answer goes back to the window
/// on the same (Revit's) thread. A change to the set's schedule, a door or window, or a material
/// marks the window stale; its timer then asks for a fresh read.
/// </summary>
internal static class DoorWindowSession
{
    private sealed class Handler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            while (true)
            {
                Action<UIApplication>? work;
                lock (Gate) { work = Work.Count > 0 ? Work.Dequeue() : null; }
                if (work == null) return;
                try { work(app); }
                catch (Exception ex) { Diagnostics.RunTrace.Mark("Door & Window window: " + ex); }
            }
        }

        public string GetName() => "DRW Helper Enlarge Door & Window";
    }

    private static readonly object Gate = new();
    private static readonly Queue<Action<UIApplication>> Work = new();
    private static ExternalEvent? _event;
    private static DoorWindowWindow? _window;
    private static Document? _doc;
    private static DoorWindowModel? _model;
    private static UIApplication? _app;

    /// <summary>Reads made for the window (a schedule refresh counts as a change: not "stale").</summary>
    private static bool _reading;

    /// <summary>The window's link to the model: work runs on the external event.</summary>
    private sealed class Bridge : IDoorWindowModel
    {
        public void Run<T>(Func<IDoorWindowApi, T> work, Action<T> done)
        {
            lock (Gate)
                Work.Enqueue(_ =>
                {
                    if (_model == null || _window == null || _window.IsDisposed || !_doc!.IsValidObject) return;
                    T result;
                    _reading = true;
                    try { result = work(_model); }
                    finally { _reading = false; }
                    if (!_window.IsDisposed) done(result);
                });
            _event?.Raise();
        }
    }

    /// <summary>Opens the window for <paramref name="doc"/> (or brings it forward). Called from the
    /// command, in API context: the external event can only be made there.</summary>
    public static void Open(UIApplication app, Document doc, DoorWindowModel model, DoorWindowDialogData data, DoorWindowPreset initial)
    {
        if (_window is { IsDisposed: false } && _doc != null && _doc.Equals(doc))
        {
            _window.Activate();
            return;
        }
        Close();
        _event ??= ExternalEvent.Create(new Handler());
        _app = app;
        _doc = doc;
        _model = model;
        data.Model = new Bridge();
        _window = new DoorWindowWindow(data, initial);
        _window.FormClosed += (_, _) => Detach();
        // Subscribed once, never removed: Revit allows (un)subscribing only inside an API call, and
        // the window closes outside one ("Revit is currently not within an API context", 2026-10-21).
        // With no window open the handlers do nothing.
        if (!_subscribed)
        {
            app.Application.DocumentChanged += OnChanged;
            app.Application.DocumentClosing += OnClosing;
            app.ViewActivated += OnViewActivated;
            _subscribed = true;
        }
        _window.Show(new Owner(app.MainWindowHandle));
    }

    private sealed class Owner : IWin32Window
    {
        public Owner(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    private static void Close()
    {
        if (_window is { IsDisposed: false }) _window.Close();
        Detach();
    }

    private static bool _subscribed;

    private static void Detach()
    {
        _window = null;
        _model = null;
        _doc = null;
        lock (Gate) Work.Clear();
    }

    /// <summary>A change the window shows: its doors or windows, a material, a schedule (only the
    /// set's own matters; the window's read decides).</summary>
    private static void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_reading || _window == null || _window.IsDisposed || !e.GetDocument().Equals(_doc)) return;
        Document doc = e.GetDocument();
        bool relevant = e.GetDeletedElementIds().Count > 0
                        || e.GetAddedElementIds().Concat(e.GetModifiedElementIds()).Select(doc.GetElement).Any(x =>
                            x is ViewSchedule or Material
                            || x?.Category?.Id.Value is (long)BuiltInCategory.OST_Doors or (long)BuiltInCategory.OST_Windows);
        if (relevant) _window.MarkStale();
    }

    private static void OnClosing(object? sender, DocumentClosingEventArgs e)
    {
        if (e.Document.Equals(_doc)) Close();
    }

    /// <summary>Another model made active: the window belongs to the one it opened on.</summary>
    private static void OnViewActivated(object? sender, ViewActivatedEventArgs e)
    {
        if (_doc != null && !e.Document.Equals(_doc)) Close();
    }
}
