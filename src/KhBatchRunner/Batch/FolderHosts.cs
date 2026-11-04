using KhBatchRunner.Forma;
using KhBatchRunner.Presets;
using KhBatchRunner.UI;

namespace KhBatchRunner.Batch;

/// <summary>
/// Builds an ad-hoc model preset from a Forma folder scan — the "hosts = every model in this
/// folder" source shared by Internal Relink and Correct Workset, and the re-scan every
/// folder-backed model set goes through at run time (<see cref="Presets.PresetResolver"/>).
///
/// <para>Models this Revit could not process are left out here, and SAID SO rather than dropped
/// quietly (<see cref="LastSkipped"/>). A folder-backed set is re-read on every run and never
/// passes through the browser, so the greying the browser does would not otherwise reach it — and a
/// set that silently shrinks is worse than one that explains itself. The rule is the browser's own,
/// <see cref="FormaModelItem.BlockedReason"/>, so the two cannot drift apart.</para>
/// </summary>
public static class FolderHosts
{
    /// <summary>
    /// Why each model the last <see cref="BuildPreset"/> left out was left out — "&lt;name&gt;:
    /// &lt;reason&gt;". Empty when everything in the folder can run.
    ///
    /// A plain field rather than an out-parameter for the same reason
    /// <see cref="DataManagementClient.LastScanNote"/> is one: the callers that most need it reach
    /// this through <see cref="Presets.PresetResolver"/>, several layers down.
    /// </summary>
    public static IReadOnlyList<string> LastSkipped { get; private set; } = Array.Empty<string>();

    public static ModelPreset BuildPreset(DataManagementClient client, FolderRef folder, bool includeSubfolders)
    {
        var preset = new ModelPreset { Name = $"{folder.Name} (folder scan)" };
        var skipped = new List<string>();
        string running = HostServices.RunningRevitVersion;

        foreach (FormaInventoryRow row in client.GetFolderInventory(folder.ToEntry(), includeSubfolders))
        {
            string? blocked = row.AsModelItem().BlockedReason(running);
            if (blocked != null)
            {
                skipped.Add($"{row.ModelName}: {blocked}");
                continue;
            }

            // Belt and braces behind the rule above: a model with no cloud GUID cannot be opened by
            // GUID whatever else is true of it, and BlockedReason already refuses those.
            if (row.ModelGuid == Guid.Empty) continue;

            preset.Models.Add(new CloudModel
            {
                DisplayName = row.ModelName,
                Region = row.Region,
                ProjectGuid = row.ProjectGuid,
                ModelGuid = row.ModelGuid,
                // Carried since 1.5.0. A folder-backed set is re-scanned through here on EVERY run,
                // and this built each model with the four fields above only — so the re-scan handed
                // back models stripped of the lineage URN and DM project id that publishing is keyed
                // on, and of any way to say which account they belong to.
                ItemUrn = row.ItemUrn,
                DmProjectId = row.DmProjectId,
                HubId = row.HubId
            });
        }

        LastSkipped = skipped;
        return preset;
    }
}
