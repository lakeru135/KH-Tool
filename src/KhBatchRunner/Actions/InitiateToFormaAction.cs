using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Batch;

namespace KhBatchRunner.Actions;

/// <summary>
/// Batch Initiate to Forma — publishes each opened model as a NEW cloud model in a chosen Forma
/// folder. A native port of the team's <c>Auto_01_Save as to local + Initiate to Forma</c> Dynamo
/// graph.
///
/// Categorised as a <see cref="ActionCategory.Check"/> on purpose: that makes
/// <see cref="Batch.BatchEngine"/> open the SOURCE without locking/syncing it (cloud sources open
/// DoNotDetach; local workshared sources open detached), which is right because we don't write the
/// source back — we publish a copy elsewhere.
///
/// A cloud (or workshared) document can't be handed straight to <c>SaveAsCloudModel</c> — so we
/// first <c>SaveAs</c> the open document to a temporary LOCAL <c>.rvt</c> (which detaches it from
/// any source central), then <c>SaveAsCloudModel</c> publishes that local copy to the target
/// folder (staging mechanics shared with Cut model in <see cref="PublishStaging"/>). This is the
/// team's "Save as to local + Initiate" flow.
/// </summary>
public sealed class InitiateToFormaAction : BatchActionBase
{
    private readonly PublishTarget _target;

    /// <summary>Stable display name — also the token used in a Batch Run / Schedule job's
    /// <c>ActionNames</c> to mark that the Initiate pass should run (see
    /// <c>ScheduledRunner.BuildJobPasses</c> / <c>BatchRunCommand</c>).</summary>
    public const string DisplayName = ActionNames.InitiateToForma;

    public InitiateToFormaAction(PublishTarget target) => _target = target;

    public override string Name => DisplayName;

    public override IReadOnlyList<string> Columns { get; } = new[] { "Published Name", "Destination", "Staged Local" };

    /// <summary>Statically reachable posture — see <see cref="ExportNwcAction.DeclaredWorksets"/>.</summary>
    public const WorksetPosture DeclaredWorksets = SyntheticActionPostures.InitiateToForma;

    public override WorksetPosture Worksets => DeclaredWorksets;

    public override string WorksetPostureReason =>
        "Moves the FILE to Forma. It never reads model content, and what a workset is doing has no " +
        "bearing on what gets uploaded — the saved file carries every workset either way.";

    /// <summary>Best-effort sweep of leftover staging files from earlier runs.</summary>
    public override void ConfigureOutput(string destinationFolder, string projectLogName) =>
        PublishStaging.SweepOld(PublishStaging.Dir("initiate"));

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        // Build the publish name BEFORE SaveAs — SaveAs re-titles the doc.
        string name = _target.BuildPublishName(doc.Title);

        // Stage to a local .rvt first — a cloud/workshared doc can't be SaveAsCloudModel'd directly.
        string localPath = PublishStaging.SaveAsLocal(doc, PublishStaging.Dir("initiate"), name);

        // Now a local model — publish a fresh copy into the target Forma folder.
        doc.SaveAsCloudModel(_target.AccountGuid, _target.ProjectGuid, _target.FolderUrn, name);

        return new Dictionary<string, string>
        {
            ["Published Name"] = name,
            ["Destination"] = _target.FolderPath,
            ["Staged Local"] = localPath,
        };
    }
}
