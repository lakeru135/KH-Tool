using System.IO;

namespace KhBatchRunner.Interop;

/// <summary>
/// Where one Batch Interop run's reports go. Pure path arithmetic, deliberately free of Revit
/// types so the split harness (<c>tools/LivenessSim</c>) can assert the naming rule without a
/// Revit process.
///
/// The rule earns its own file because of what parallel runs do to it. AIT names each HTML report
/// after the model itself — the add-in passes a FOLDER to the exporter and gets
/// <c>&lt;model&gt;.html</c> back — so a same-titled model in two different groups cannot be
/// disambiguated by filename from inside either process. Two groups sharing one folder means one
/// report silently overwrites the other. Groups launch together and the folder name is a
/// whole-second timestamp, so they land in the same second essentially always.
///
/// The fix is one folder per group, all under the run's shared stamp so they still read as one run:
/// <code>
/// &lt;dest&gt;\Batch Interop\InteropReports_20260809-143001_g1\
/// &lt;dest&gt;\Batch Interop\InteropReports_20260809-143001_g2\
/// </code>
/// A single-process run keeps the unsuffixed name it has always had.
/// </summary>
internal static class InteropReportPaths
{
    private const string FolderPrefix = "InteropReports_";
    private const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// Report folder for one run. Pass <paramref name="chunkTag"/> as null for a single-process
    /// run and <c>g&lt;n&gt;</c> for group n (<see cref="Schedule.ChunkContext.FolderTag"/>).
    /// </summary>
    /// <param name="stamp">The run's stamp — shared across sibling groups so their folders sort
    /// together, "now" for an ordinary run.</param>
    public static string ReportFolder(
        string destinationFolder, string actionName, DateTime stamp, string? chunkTag)
    {
        string leaf = FolderPrefix + stamp.ToString(StampFormat);
        if (!string.IsNullOrEmpty(chunkTag)) leaf += "_" + chunkTag;
        return Path.Combine(destinationFolder, Sanitize(actionName), leaf);
    }

    /// <summary>Folder/file-name-safe form of a display name (<c>Batch Interop</c> is already safe;
    /// this guards a renamed action rather than today's constant).</summary>
    private static string Sanitize(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
}
