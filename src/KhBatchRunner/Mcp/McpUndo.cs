#if !EDITION_DRW
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Mcp;

/// <summary>
/// The AI Bridge's undo and open_model tools.
///
/// <para>Revit's API cannot undo, and cannot read the undo list. So the bridge keeps its own record
/// of the AI's steps per model, from DocumentChanged: an AI call that commits pushes "AI: tool";
/// anything the user commits clears the record (their step is now the newest, and Ctrl+Z would undo
/// theirs first); an undo of the newest AI step pops it. <c>undo</c> then posts Revit's own Undo
/// command — exactly Ctrl+Z — and only while the newest step is known to be the AI's.</para>
/// </summary>
internal static partial class McpTools
{
    private static bool _watching;
    private static bool _inCall;
    private static bool _committedInCall;
    private static readonly Dictionary<string, List<string>> Steps = new(StringComparer.OrdinalIgnoreCase);

    private static string DocKey(Document d) => d.PathName.Length > 0 ? d.PathName : "unsaved:" + d.Title;

    private static List<string> StepsOf(Document d)
    {
        string key = DocKey(d);
        if (!Steps.TryGetValue(key, out List<string>? s)) Steps[key] = s = new List<string>();
        return s;
    }

    /// <summary>Subscribed on the first call (an API context) and kept for the session: cheap, and
    /// steps made while the bridge was off are the user's, which the handler must still see.</summary>
    private static void WatchUndo(UIApplication app)
    {
        if (_watching) return;
        app.Application.DocumentChanged += OnDocumentChanged;
        app.Application.DocumentClosing += (_, e) => Steps.Remove(DocKey(e.Document));
        _watching = true;
    }

    private static void BeginAiCall()
    {
        _inCall = true;
        _committedInCall = false;
    }

    /// <summary>Whether the call committed anything (and so left a step in the undo list).</summary>
    private static bool EndAiCall()
    {
        _inCall = false;
        return _committedInCall;
    }

    private static void PushAiStep(Document doc, string name) => StepsOf(doc).Add(name);

    private static JsonArray AiSteps(Document doc) =>
        new(StepsOf(doc).AsEnumerable().Reverse().Select(n => (JsonNode)n).ToArray());

    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_inCall)
        {
            if (e.Operation == UndoOperation.TransactionCommitted) _committedInCall = true;
            return;
        }
        List<string> steps = StepsOf(e.GetDocument());
        IList<string> names = e.GetTransactionNames();
        switch (e.Operation)
        {
            case UndoOperation.TransactionCommitted:
                steps.Clear();
                break;
            case UndoOperation.TransactionUndone:
                if (steps.Count > 0 && names.Contains(steps[steps.Count - 1])) steps.RemoveAt(steps.Count - 1);
                else steps.Clear();
                break;
            case UndoOperation.TransactionRedone:
                if (names.Count > 0 && names.All(n => n.StartsWith("AI: ", StringComparison.Ordinal))) steps.AddRange(names);
                else steps.Clear();
                break;
        }
    }

    private static JsonNode Undo(UIApplication app, Document doc)
    {
        List<string> steps = StepsOf(doc);
        if (steps.Count == 0)
            throw new McpError("Nothing to undo: the newest step in Revit's undo list is not an AI call (the user changed " +
                               "something since, or no AI call has changed this model). Ask the user to press Ctrl+Z in Revit " +
                               "if they want their own step undone.");
        string undoing = steps[steps.Count - 1];
        // Revit greys Undo out while this call (itself an external command) runs, so PostCommand
        // refuses it, and Idling only ticks once someone moves the mouse. Revit's main window is
        // MFC: Edit > Undo is the standard ID_EDIT_UNDO command. Posted, it waits in the message
        // queue and runs right after this call returns — the same as one Ctrl+Z, whoever is at the PC.
        if (!PostMessage(app.MainWindowHandle, WmCommand, (IntPtr)MfcEditUndo, IntPtr.Zero))
            throw new McpError("Could not reach Revit's main window to undo. Ask the user to press Ctrl+Z.");
        _lastUndo = $"'{undoing}': posted to Revit";
        return new JsonObject
        {
            ["posted"] = true,
            ["undoing"] = undoing,
            ["ai_steps_left_after"] = steps.Count - 1,
            ["note"] = "Revit runs the undo as soon as this call returns. Call kh_status to confirm: '" + undoing +
                       "' leaves ai_steps (last_undo says what happened)."
        };
    }

    private const uint WmCommand = 0x0111;
    private const int MfcEditUndo = 0xE12B;
    private static string? _lastUndo;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static JsonNode OpenModel(UIApplication app, string path)
    {
        if (!path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
            throw new McpError("open_model opens .rvt models and .rfa families only.");
        if (!File.Exists(path)) throw new McpError($"No file at '{path}'.");
        string full = Path.GetFullPath(path);

        Document? active = app.ActiveUIDocument?.Document;
        if (active != null && string.Equals(active.PathName, full, StringComparison.OrdinalIgnoreCase))
            return Status(app);

        // A central model opens detached: the AI must never sync anything into a shared model.
        BasicFileInfo info = BasicFileInfo.Extract(full);
        ModelPath modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(full);
        var options = new OpenOptions();
        if (info.IsWorkshared && info.IsCentral)
            options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
        // Nobody may be at the PC to click: answer the open's dialogs (missing links, upgrades…) the
        // way a person opening it to look would, and say which were answered.
        var answered = new JsonArray();
        void onDialog(object? s, Autodesk.Revit.UI.Events.DialogBoxShowingEventArgs e)
        {
            string text = e is Autodesk.Revit.UI.Events.TaskDialogShowingEventArgs td ? td.Message : "";
            if (OpenDialogAnswers(e.DialogId).Any(e.OverrideResult))
                answered.Add($"{e.DialogId}: {text}".Trim().TrimEnd(':'));
        }
        app.DialogBoxShowing += onDialog;
        try
        {
            app.OpenAndActivateDocument(modelPath, options, false);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            throw new McpError($"Revit could not open '{Path.GetFileName(full)}': {ex.Message}");
        }
        finally
        {
            app.DialogBoxShowing -= onDialog;
        }
        JsonNode status = Status(app);
        if (info.IsWorkshared && info.IsCentral) status["opened_detached"] = true;
        if (answered.Count > 0) status["dialogs_answered"] = answered;
        return status;
    }

    /// <summary>The answers to try, in order, for a dialog Revit shows while opening: "Ignore and
    /// continue opening" for missing links (command link 1002); otherwise OK, Yes, then Close —
    /// the first one the dialog has is taken.</summary>
    private static IEnumerable<int> OpenDialogAnswers(string dialogId)
    {
        if (dialogId == "TaskDialog_Unresolved_References") yield return 1002;
        yield return (int)TaskDialogResult.Ok;
        yield return (int)TaskDialogResult.Yes;
        yield return (int)TaskDialogResult.Close;
    }
}
#endif
