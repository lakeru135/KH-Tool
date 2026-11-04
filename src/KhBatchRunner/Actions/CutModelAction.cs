using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Batch;

namespace KhBatchRunner.Actions;

/// <summary>
/// Forma Batch Action — "Cut model": publishes a site-specific copy of each model. A native port of
/// the team's "Cut to" variants of <c>Auto_01_Save as to local + Initiate to Forma</c> (site step
/// from <c>Auto_00_Set Loc and Purge Option</c>). Per model:
/// <list type="number">
/// <item>Fail-fast: confirm the model HAS a site/project location matching the SITE CODE — exact
/// name first, else the alphabetically-first prefix match (input "BETA2A" matches location
/// "BETA2A-AR"). A model with no matching location FAILS with a clear ERROR row BEFORE anything is
/// written to disk, and is NOT published.</item>
/// <item>SaveAs to a local temp <c>.rvt</c> — detaches from any source central, so every edit
/// below lands on the TEMP copy; the source model is never modified.</item>
/// <item>On that temp copy, activate the matching location, optionally deleting the others.</item>
/// <item>Publish to the target Forma folder; the same site code is the name prefix
/// (<see cref="PublishPrefixMode.Rename"/>: "XXX-AR" → "BETA2A-AR", mirroring the location name).</item>
/// </list>
/// Category stays Check (like Initiate): the engine opens the source without locking/syncing it —
/// the site-location edits persist only into the NEW cloud model.
/// </summary>
public sealed class CutModelAction : BatchActionBase
{
    public const string DisplayName = ActionNames.CutModel;

    private readonly PublishTarget _target;
    private readonly bool _deleteOtherLocations;
    private string _note = "";

    /// <summary>The site code is carried as <see cref="PublishTarget.Prefix"/> — one input
    /// drives both the location match and the publish name prefix.</summary>
    public CutModelAction(PublishTarget target, bool deleteOtherLocations)
    {
        _target = target;
        _deleteOtherLocations = deleteOtherLocations;
    }

    public override string Name => DisplayName;

    /// <summary>Statically reachable posture — see <see cref="ExportNwcAction.DeclaredWorksets"/>.</summary>
    public const WorksetPosture DeclaredWorksets = SyntheticActionPostures.CutModel;

    public override WorksetPosture Worksets => DeclaredWorksets;

    public override string WorksetPostureReason =>
        "Activates one ProjectLocation and deletes the others. Locations are document-level, not " +
        "workset-owned content, and the edit happens on a temp copy.";

    public override IReadOnlyList<string> Columns { get; } = new[]
        { "Published Name", "Destination", "Activated Location", "Locations Removed", "Staged Local" };

    public override string PerModelNote => _note;

    public override void ConfigureOutput(string destinationFolder, string projectLogName) =>
        PublishStaging.SweepOld(PublishStaging.Dir("cutmodel"));

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        _note = "";
        string siteCode = _target.Prefix;
        if (string.IsNullOrWhiteSpace(siteCode))
            throw new InvalidOperationException("Cut model needs a site code (used as location match and name prefix).");

        // Build the publish name BEFORE SaveAs — SaveAs re-titles the doc.
        string name = _target.BuildPublishName(doc.Title);

        // 1. Fail-fast: the model MUST have a location matching the site code. Check BEFORE the
        //    expensive local SaveAs so a mismatch produces the same ERROR row without writing a
        //    temp file.
        if (FindLocation(doc, siteCode) is null)
            throw new InvalidOperationException(
                $"No site location matching '{siteCode}' — model has: {ListLocationNames(doc)}. Not published.");

        // 2. Stage to a local temp .rvt (no transaction may be open around SaveAs).
        //    From here on, doc IS the temp copy — the source is untouched.
        string localPath = PublishStaging.SaveAsLocal(doc, PublishStaging.Dir("cutmodel"), name);

        // 3. Site location — re-resolve on the temp copy for a fresh handle; all edits on that copy,
        //    in one transaction.
        ProjectLocation? match = FindLocation(doc, siteCode);
        if (match is null)
            throw new InvalidOperationException(
                $"No site location matching '{siteCode}' — model has: {ListLocationNames(doc)}. Not published.");

        int removed = 0;
        var stubborn = new List<string>();
        using (var t = new Transaction(doc, "Cut model — set site location"))
        {
            t.Start();
            // Activate FIRST — Revit refuses to delete the active location.
            doc.ActiveProjectLocation = match;
            if (_deleteOtherLocations)
            {
                foreach (ProjectLocation other in doc.ProjectLocations
                             .Cast<ProjectLocation>().Where(l => l.Id != match.Id).ToList())
                {
                    try { doc.Delete(other.Id); removed++; }
                    catch { stubborn.Add(other.Name); }
                }
            }
            t.Commit();
        }

        // 4. Publish the cut copy into the target Forma folder (outside any transaction).
        doc.SaveAsCloudModel(_target.AccountGuid, _target.ProjectGuid, _target.FolderUrn, name);

        _note = $"Activated '{match.Name}'"
            + (_deleteOtherLocations ? $"; removed {removed} other location(s)" : "")
            + (stubborn.Count > 0 ? $"; could not remove: {string.Join(", ", stubborn)}" : "");

        return new Dictionary<string, string>
        {
            ["Published Name"] = name,
            ["Destination"] = _target.FolderPath,
            ["Activated Location"] = match.Name,
            ["Locations Removed"] = _deleteOtherLocations ? removed.ToString() : "(kept)",
            ["Staged Local"] = localPath,
        };
    }

    /// <summary>Exact case-insensitive name match first; else the alphabetically-first
    /// location whose name starts with the site code.</summary>
    private static ProjectLocation? FindLocation(Document doc, string siteCode)
    {
        var locations = doc.ProjectLocations.Cast<ProjectLocation>().ToList();
        return locations.FirstOrDefault(l => l.Name.Equals(siteCode, StringComparison.OrdinalIgnoreCase))
               ?? locations.Where(l => l.Name.StartsWith(siteCode, StringComparison.OrdinalIgnoreCase))
                           .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                           .FirstOrDefault();
    }

    private static string ListLocationNames(Document doc) =>
        string.Join(", ", doc.ProjectLocations.Cast<ProjectLocation>()
            .Select(l => l.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
}
