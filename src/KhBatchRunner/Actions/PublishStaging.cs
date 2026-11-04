using System.IO;
using Autodesk.Revit.DB;

namespace KhBatchRunner.Actions;

/// <summary>
/// Shared staging mechanics for actions that publish a COPY of the open model to Forma
/// (<see cref="InitiateToFormaAction"/>, <see cref="CutModelAction"/>): a per-feature temp folder
/// under <c>%temp%\KhBatchRunner\</c>, a best-effort sweep of stale leftovers, and the
/// SaveAs-to-local hop that detaches the doc from any source central (a cloud/workshared doc
/// can't be handed straight to <c>SaveAsCloudModel</c> — Revit throws "This Document is a cloud
/// model, cannot be saved as a cloud model").
/// </summary>
internal static class PublishStaging
{
    public static string Dir(string subdir) =>
        Path.Combine(Path.GetTempPath(), "KhBatchRunner", subdir);

    /// <summary>Best-effort delete of staging files older than 2h — run from
    /// <c>ConfigureOutput</c>, before this run creates its own GUID-named file, so it never
    /// touches an in-flight publish.</summary>
    public static void SweepOld(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.EnumerateFiles(dir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-2))
                        File.Delete(f);
                }
                catch { /* locked / gone — skip */ }
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// SaveAs the open doc to a GUID-suffixed local <c>.rvt</c> in <paramref name="dir"/>
    /// (SaveAsCentral when workshared) and return the path. Must be called with NO open
    /// transaction — Revit forbids SaveAs inside one.
    /// </summary>
    public static string SaveAsLocal(Document doc, string dir, string name)
    {
        Directory.CreateDirectory(dir);
        string localPath = Path.Combine(dir, name + "_" + Guid.NewGuid().ToString("N") + ".rvt");
        var saveOpts = new SaveAsOptions { OverwriteExistingFile = true };
        if (doc.IsWorkshared)
            saveOpts.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
        doc.SaveAs(localPath, saveOpts);
        return localPath;
    }
}
