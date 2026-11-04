using Autodesk.Revit.DB;

namespace KhBatchRunner.Reporting;

/// <summary>Shared helpers for the run-end matrix actions (model-name cleanup + output path).</summary>
internal static class MatrixUtil
{
    /// <summary>The model's display name for a matrix column — <see cref="Document.Title"/> with a
    /// trailing "_detached" (added when a local model is opened detached) stripped.</summary>
    public static string ModelName(Document doc)
    {
        string title = doc.Title ?? "";
        const string suffix = "_detached";
        return title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? title.Substring(0, title.Length - suffix.Length)
            : title;
    }

    /// <summary>
    /// Whether THIS process should write the run-end matrix itself.
    ///
    /// <para>False inside an auto-split chunk. Each chunk only ever sees the models of its own
    /// group, and stamps its filename with its own finish second, so letting every chunk write
    /// produced one partial workbook per group — the "the matrix is not a single file when I run a
    /// split" report. The whole run's matrix is built once instead, after the last chunk exits, by
    /// <see cref="RunEndMatrix.BuildForRun"/> from the detail CSV the chunks already share.</para>
    /// </summary>
    public static bool WritesHere => Schedule.ChunkContext.GroupNumber == 0;

    /// <summary>The matrix path for a task — see <see cref="MatrixPaths.OutputPath"/>, which holds
    /// the Revit-free half of this helper so the run-end merge can share it.</summary>
    /// <param name="runStamp">The run's shared stamp (<see cref="Actions.IBatchAction.RunStamp"/>).
    /// Pass it: without it the workbook is named for the second the write finished, which is a
    /// DIFFERENT name from the one the run-end merge would give the same run.</param>
    public static string OutputPath(string destinationFolder, string projectLog, string taskName,
        DateTime? runStamp = null, string label = "Matrix") =>
        MatrixPaths.OutputPath(destinationFolder, projectLog, taskName, runStamp, label);
}
