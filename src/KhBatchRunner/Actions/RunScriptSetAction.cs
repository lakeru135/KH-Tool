using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Scripts;

namespace KhBatchRunner.Actions;

/// <summary>
/// Runs every <c>.dyn</c> in a <see cref="ScriptSet"/>, in order, against each opened model —
/// the Phase-4 Dynamo runner. Each graph executes headless via <see cref="DynamoInvoker"/>
/// (journal-automation mode, no Dynamo window). Categorised <see cref="ActionCategory.Action"/>
/// because the engine can't know what a graph does to the model — the run's
/// <see cref="Batch.SyncMode"/> decides whether any changes are kept, and a dry run makes an
/// unknown script effectively read-only. <see cref="NeedsActiveDocument"/> is the hard
/// constraint (spike-verified): Dynamo binds a graph to the ACTIVE document, so this pass's
/// models must be opened activated, never in the background.
///
/// Constructed per run from the checked script set (like <see cref="InitiateToFormaAction"/>,
/// it carries config, so it is deliberately NOT in <see cref="ActionRegistry"/> — the pass
/// builders create it from <see cref="ScriptSetStore"/> by name).
/// </summary>
public sealed class RunScriptSetAction : BatchActionBase
{
    private readonly ScriptSet _set;
    private string _lastNote = "";

    public RunScriptSetAction(ScriptSet set) => _set = set;

    // "DYN_" (not "DYN: ") so the sanitized log/detail folder name is "DYN_<Name>" with no stray
    // space — SanitizeFileName splits on the invalid ':' but not the space after it, which used to
    // leave "DYN_ <Name>".
    public override string Name => "DYN_" + _set.Name;

    public override ActionCategory Category => ActionCategory.Action;

    public override bool NeedsActiveDocument => true;

    public override IReadOnlyList<string> Columns => new[] { "Script", "Result", "Note" };

    public override WorksetPosture Worksets => WorksetPosture.PartialByDesign;

    public override string WorksetPostureReason =>
        "This action collects nothing itself — the .dyn does, and what a graph collects is unknown " +
        "here. NeedsActiveDocument does NOT change the posture: the activated open closes every " +
        "user workset too unless the action opts in (BatchEngine's activated path), so ANY graph " +
        "using a FilteredElementCollector over instances sees a partial model and returns a " +
        "plausible, wrong answer. Declared partial on 2026-08-16 as the honest description of a " +
        "graph the add-in cannot inspect. A graph that needs the model whole must say so — this is " +
        "the one posture that cannot be settled in this file.";

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc) =>
        RunDetailRows(uiApp, doc) is { Count: > 0 } rows
            ? rows[rows.Count - 1]
            : new Dictionary<string, string> { ["Script"] = "(none)", ["Result"] = "SKIPPED", ["Note"] = "script set is empty" };

    public override IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(UIApplication uiApp, Document doc)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        int ok = 0, failed = 0;
        string firstError = "";
        foreach (string path in _set.ScriptPaths)
        {
            string result, note;
            try
            {
                string outcome = DynamoInvoker.RunGraph(uiApp, path);
                bool success = outcome == "Succeeded";
                result = success ? "OK" : "ERROR";
                note = success ? "" : "Dynamo returned " + outcome;
                if (success) ok++; else failed++;
            }
            catch (Exception ex)
            {
                result = "ERROR";
                note = ex.Message;
                failed++;
            }
            if (result == "ERROR" && firstError.Length == 0)
                firstError = $"{Path.GetFileName(path)}: {note}";
            rows.Add(new Dictionary<string, string>
            {
                ["Script"] = Path.GetFileName(path),
                ["Result"] = result,
                ["Note"] = note
            });
        }

        _lastNote = failed == 0
            ? $"{ok} script(s) ok"
            : $"{ok} ok / {failed} failed";

        // Every script failing is a model-level failure — throw so the engine writes an ERROR
        // tracking row. The engine only logs detail rows once this returns, so the throw eats
        // them — carry the first script error in the message. A partial failure stays OK, with
        // the roll-up note flagging it and the detail rows carrying each script's error.
        if (failed > 0 && ok == 0)
            throw new InvalidOperationException($"all {failed} script(s) failed — first: {firstError}");

        return rows;
    }

    public override string PerModelNote => _lastNote;
}
