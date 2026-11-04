using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Actions;

/// <summary>
/// Dev-only seed action for the publish test loop: stamps Project Information's Status with
/// the current time so a cloud workshared model reliably has an UNPUBLISHED change after the
/// engine's SynchronizeWithCentral (the C4R publish command errors on an up-to-date model,
/// so the loop needs a real synced edit to publish). Unlike the <c>#if DEBUG</c> seed
/// actions this compiles in Release — the loop runs against the installed build — but it is
/// only registered when <c>KHBATCH_DEV_ACTIONS=1</c> (see <see cref="ActionRegistry"/>),
/// so it never appears on a user's machine. Project Information lives on a system workset,
/// which the engine's closed-worksets open always loads, so no workset reopen is needed.
/// </summary>
public sealed class SeedParamTouchAction : BatchActionBase
{
    public override string Name => "DEV Seed Param Touch";

    public override WorksetPosture Worksets => WorksetPosture.ReadsNoInstances;

    public override string WorksetPostureReason =>
        "Writes doc.ProjectInformation, which the closed open always loads — the reason this " +
        "fixture exists in the shape it does.";

    public override ActionCategory Category => ActionCategory.Action;

    public override IReadOnlyList<string> Columns { get; } = new[] { "Result" };

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        string stamp = $"SeedTouch {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

        using var t = new Transaction(doc, "DEV Seed Param Touch");
        t.Start();
        doc.ProjectInformation.Status = stamp;
        t.Commit();

        return new Dictionary<string, string> { ["Result"] = $"Project Status = '{stamp}'" };
    }
}
