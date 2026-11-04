using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Reporting;

namespace KhBatchRunner.Actions;

/// <summary>
/// Test action: read every design option set and design option in the model, and which option
/// in each set is primary. The tracking log gets one summary row per model (set/option counts);
/// the detail log gets one row per design option (<see cref="RunDetailRows"/>), not a joined-cell
/// summary. Read-only — no transaction. A batch port of the team's "Check_Get Design Option.dyn"
/// Model-QA graph.
/// </summary>
public sealed class GetDesignOptionAction : BatchActionBase
{
    public override string Name => ActionNames.GetDesignOption;

    public override IReadOnlyList<string> Columns { get; } = new[] { "Design Set", "Design Option", "Is Primary" };

    public override WorksetPosture Worksets => WorksetPosture.PartialByDesign;

    public override string WorksetPostureReason =>
        "Collects DesignOption, which is an element with a WorksetId — so whether a closed open " +
        "loads them is a question about Revit's internals that reading the code cannot settle. " +
        "UNPROVEN either way as of 2026-08-16, declared partial because that is the safe reading " +
        "of an unknown. Settling it needs one live run against a model with design options in a " +
        "user workset: if the set comes back complete, this becomes ReadsNoInstances.";

    // Accumulated across every model in the run so OnRunComplete can pivot them into the matrix.
    private readonly List<(string Model, string Set, string Option, bool IsPrimary)> _matrixRows = new();
    private string _destinationFolder = "";
    private string _projectLog = "";

    public override void ConfigureOutput(string destinationFolder, string projectLogName)
    {
        _destinationFolder = destinationFolder;
        _projectLog = projectLogName;
    }

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        var (setCount, options) = Collect(doc);
        return new Dictionary<string, string>
        {
            ["Total Option Sets"] = setCount.ToString(),
            ["Total Options"] = options.Count.ToString(),
        };
    }

    public override IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(UIApplication uiApp, Document doc)
    {
        var (_, options) = Collect(doc);
        string model = MatrixUtil.ModelName(doc);
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var o in options)
        {
            rows.Add(new Dictionary<string, string>
            {
                ["Design Set"] = o.SetName,
                ["Design Option"] = o.OptionName,
                ["Is Primary"] = o.IsPrimary ? "Yes" : "No",
            });
            _matrixRows.Add((model, o.SetName, o.OptionName, o.IsPrimary));
        }
        return rows;
    }

    /// <summary>Pivot the run's design options into the matrix workbook beside this task's detail CSV.
    /// Skipped inside an auto-split chunk, which only holds part of the run — see
    /// <see cref="MatrixUtil.WritesHere"/>.</summary>
    public override void OnRunComplete()
    {
        if (!MatrixUtil.WritesHere || _matrixRows.Count == 0) return;
        string path = MatrixUtil.OutputPath(_destinationFolder, _projectLog, Name, RunStamp);
        DesignOptionMatrix.Write(path, _matrixRows);
    }

    private static (int SetCount, List<(string SetName, string OptionName, bool IsPrimary)> Options) Collect(Document doc)
    {
        // DesignOptionSet is not a public API type, so we don't collect it directly. Instead each
        // DesignOption (public) points at its parent set via the OPTION_SET_ID parameter; the set
        // itself is a regular Element whose Name reads through doc.GetElement. Distinct set ids
        // across all options give the set count.
        var options = new List<(string SetName, string OptionName, bool IsPrimary)>();
        var setIds = new HashSet<ElementId>();
        foreach (DesignOption opt in new FilteredElementCollector(doc)
                     .OfClass(typeof(DesignOption)).Cast<DesignOption>())
        {
            ElementId setId = opt.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId()
                              ?? ElementId.InvalidElementId;
            string setName = "";
            if (setId != ElementId.InvalidElementId)
            {
                setIds.Add(setId);
                setName = doc.GetElement(setId)?.Name ?? "";
            }
            options.Add((setName, opt.Name, opt.IsPrimary));
        }

        return (setIds.Count, options);
    }
}
