using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Reporting;

namespace KhBatchRunner.Actions;

/// <summary>
/// Test action: read every phase and every user workset in the model. The tracking log
/// gets one summary row per model (counts); the detail log gets one row per phase and one
/// row per workset (<see cref="RunDetailRows"/>), not a joined-cell summary. Read-only —
/// no transaction needed.
/// </summary>
public sealed class GetPhaseAndWorksetAction : BatchActionBase
{
    public override string Name => ActionNames.GetPhaseAndWorkset;

    public override IReadOnlyList<string> Columns { get; } = new[] { "Category", "Name" };

    public override WorksetPosture Worksets => WorksetPosture.ReadsNoInstances;

    public override string WorksetPostureReason =>
        "Lists the phases and the worksets themselves. A closed workset still EXISTS and still has " +
        "a name — closing it hides its contents, not the workset.";

    // Accumulated across every model in the run so OnRunComplete can pivot them into the matrix.
    private readonly List<(string Model, string Category, string Name)> _matrixRows = new();
    private string _destinationFolder = "";
    private string _projectLog = "";
    private string _perModelNote = "";

    /// <summary>
    /// What this model actually yielded, one line in the tracking log's Message cell.
    ///
    /// <para><see cref="Run"/> already computes exactly this — and the batch engine never calls it.
    /// <see cref="Batch.BatchEngine.RunOnOpenDocument"/> calls <see cref="RunDetailRows"/> and reads
    /// <see cref="PerModelNote"/>; the summary dictionary goes nowhere. So the run that shipped a
    /// matrix with no Workset sheet had 107 tracking rows with a blank Message and no record
    /// anywhere of whether a single model was workshared. Producing the evidence where the engine
    /// reads it is the whole fix.</para>
    /// </summary>
    public override string PerModelNote => _perModelNote;

    public override void ConfigureOutput(string destinationFolder, string projectLogName)
    {
        _destinationFolder = destinationFolder;
        _projectLog = projectLogName;
    }

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        var (phases, worksets) = ReadPhasesAndWorksets(doc);
        return new Dictionary<string, string>
        {
            ["Workshared"] = doc.IsWorkshared ? "Yes" : "No",
            ["Phase Count"] = phases.Count.ToString(),
            ["Workset Count"] = worksets.Count.ToString(),
        };
    }

    public override IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(UIApplication uiApp, Document doc)
    {
        var (phases, worksets) = ReadPhasesAndWorksets(doc);
        _perModelNote = $"{phases.Count} phase(s), " +
                        (doc.IsWorkshared ? $"{worksets.Count} workset(s)" : "not workshared");
        string model = MatrixUtil.ModelName(doc);
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (string phase in phases)
        {
            rows.Add(new Dictionary<string, string> { ["Category"] = "Phase", ["Name"] = phase });
            _matrixRows.Add((model, "Phase", phase));
        }
        foreach (string workset in worksets)
        {
            rows.Add(new Dictionary<string, string> { ["Category"] = "Workset", ["Name"] = workset });
            _matrixRows.Add((model, "Workset", workset));
        }
        return rows;
    }

    /// <summary>Pivot the run's phases/worksets into the matrix workbook beside this task's detail CSV.
    /// Skipped inside an auto-split chunk, which only holds part of the run — see
    /// <see cref="MatrixUtil.WritesHere"/>.</summary>
    public override void OnRunComplete()
    {
        if (!MatrixUtil.WritesHere || _matrixRows.Count == 0) return;
        string path = MatrixUtil.OutputPath(_destinationFolder, _projectLog, Name, RunStamp);
        PhaseWorksetMatrix.Write(path, _matrixRows);
    }

    private static (List<string> Phases, List<string> Worksets) ReadPhasesAndWorksets(Document doc)
    {
        // Phases (ordered by sequence).
        var phases = new List<string>();
        foreach (Phase phase in doc.Phases)
            phases.Add(phase.Name);

        // User worksets (only meaningful in a workshared model).
        var worksets = new List<string>();
        if (doc.IsWorkshared)
        {
            var collector = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset);
            foreach (Workset ws in collector)
                worksets.Add(ws.Name);
        }

        return (phases, worksets);
    }
}
