using System.IO;

namespace KhBatchRunner.Reporting;

/// <summary>
/// Where a run-end matrix workbook goes.
///
/// Separate from <see cref="MatrixUtil"/> — which is the one file in this folder that touches the
/// Revit API, and is therefore the one file excluded from the controller project — because the
/// merge that builds the matrix for an auto-split run (<see cref="RunEndMatrix"/>) runs in the
/// supervising process and needs the same path, without Revit.
/// </summary>
internal static class MatrixPaths
{
    /// <summary>
    /// The matrix path for a task: <c>&lt;dest&gt;\&lt;Task&gt;\&lt;Datetime&gt;_&lt;Project&gt;_&lt;Task&gt;
    /// Matrix.xlsx</c> — beside that task's detail CSV in the per-task folder.
    /// </summary>
    /// <param name="stamp">The run's timestamp. Pass the run's SHARED stamp whenever one exists, so
    /// the workbook's name matches the run's tracking log rather than the second the writer happened
    /// to finish — which for a split run differed per group and was half of why a split produced
    /// several workbooks instead of one.</param>
    /// <param name="label">The word after the task name — "Matrix" for the pivots, "Summary" for the
    /// Batch Interop run workbook, which is the same kind of run-end file in the same place.</param>
    public static string OutputPath(string destinationFolder, string projectLog, string taskName,
        DateTime? stamp = null, string label = "Matrix")
    {
        string taskDir = Path.Combine(destinationFolder, Sanitize(taskName));
        string file = Sanitize($"{stamp ?? DateTime.Now:yyyyMMdd_HHmmss}_{projectLog}_{taskName} {label}") + ".xlsx";
        return Path.Combine(taskDir, file);
    }

    private static string Sanitize(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
}
