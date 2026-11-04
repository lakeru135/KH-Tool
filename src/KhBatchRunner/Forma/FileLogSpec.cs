namespace KhBatchRunner.Forma;

/// <summary>
/// Which files a Get File Log extract reports on.
///
/// <para>Two choices, deliberately. 1.4.4 briefly offered a third — a free-text list of extensions
/// (<c>TheseTypes = 2</c>) — which was more configuration than the job needs: in practice a file
/// log is either about the models or about everything in the folder. Any other value read back
/// from an older job falls through to <see cref="AllFiles"/>; see <see cref="FileLogSpec.Accepts"/>.</para>
/// </summary>
public enum FileLogScope
{
    /// <summary>Every file in the folder, whatever its type. The default.</summary>
    AllFiles = 0,
    /// <summary>Only <c>.rvt</c> — what the extract did unconditionally before 1.4.4.</summary>
    RevitOnly = 1,
}

/// <summary>
/// The options a Get File Log extract runs under — shared by the interactive window and the
/// schedulable step, and persisted inside a <see cref="Batch.FileLogInputSet"/>.
///
/// <para><b>Why this exists.</b> The extract used to test <c>displayName.EndsWith(".rvt")</c> and
/// drop everything else, so a folder's DWGs, IFCs and PDFs had no version history anyone could
/// pull. Nothing about the report was ever Revit-specific: every column it writes
/// (<c>versionNumber</c>, <c>lastModifiedTime</c>, <c>lastModifiedUserName</c>, <c>storageSize</c>,
/// <c>processState</c>, <c>comment</c>) is a generic Data Management version attribute that a PDF
/// populates exactly as a model does. The filter was an accident of the feature's origin — a port
/// of a Dynamo graph that happened to be about models.</para>
///
/// <para><b>A null spec means <see cref="FileLogScope.AllFiles"/>.</b> Deliberate: a job saved
/// before 1.4.4 has no such property, deserializes to null, and reports every file — the behaviour
/// asked for — with no migration step. A job that wants the old shape says so.</para>
///
/// <para>Plain class with settable properties rather than a record, matching
/// <c>ExportNwcInputSet</c>: these round-trip through System.Text.Json on net48 as well as net8.</para>
/// </summary>
public sealed class FileLogSpec
{
    public FileLogScope Scope { get; set; } = FileLogScope.AllFiles;

    /// <summary>
    /// Report only each file's newest version instead of its whole history.
    ///
    /// <para>Not just less output — far less WORK. The full history needs one extra REST call per
    /// item unconditionally, so a folder of 3,000 PDFs costs 3,000 round-trips that a Revit-only
    /// crawl never paid. The tip version normally arrives inside the folder-contents response
    /// already, so this mode usually costs nothing beyond the folder listing; only an item whose
    /// tip is missing from that response falls back to a request of its own.</para>
    /// </summary>
    public bool LatestVersionOnly { get; set; }

    /// <summary>Include items Forma marks deleted (<c>attributes.hidden</c>). Off by default: with
    /// every file type in scope, a folder's deleted files would otherwise appear alongside its
    /// live ones with nothing to tell them apart.</summary>
    public bool IncludeHidden { get; set; }

    /// <summary>The spec a caller that passed none gets — every file, every version.</summary>
    public static FileLogSpec Default => new();

    /// <summary>The pre-1.4.4 behaviour, for a caller that wants it back.</summary>
    public static FileLogSpec RevitOnly => new() { Scope = FileLogScope.RevitOnly };

    /// <summary>Deep copy — a set carrying this is snapshotted onto a job, and a split run's
    /// children are built from that snapshot.</summary>
    public FileLogSpec Clone() => new()
    {
        Scope = Scope,
        LatestVersionOnly = LatestVersionOnly,
        IncludeHidden = IncludeHidden,
    };

    /// <summary>A file name's extension, lowercased and without its dot; "" when it has none.</summary>
    public static string ExtensionOf(string displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return "";
        int dot = displayName.LastIndexOf('.');
        // A dot that is the last character, or part of a leading ".name", is not an extension.
        if (dot <= 0 || dot == displayName.Length - 1) return "";
        return displayName.Substring(dot + 1).ToLowerInvariant();
    }

    /// <summary>Whether this extract should report <paramref name="displayName"/>.</summary>
    public bool Accepts(string displayName) =>
        // Anything that is not RevitOnly means everything — including a scope value this build has
        // no name for, which is how a job saved by 1.4.4 with the withdrawn "these types" scope
        // reads back. Reporting too much is recoverable by eye; reporting nothing looks broken.
        Scope != FileLogScope.RevitOnly || ExtensionOf(displayName) == "rvt";

    /// <summary>How to describe this scope in a "nothing found" message.</summary>
    public string ScopeDescription() =>
        Scope == FileLogScope.RevitOnly ? ".rvt models" : "files";

    /// <summary>One line for a dropdown — what this spec reports, and how much of it.</summary>
    public string Label() =>
        (Scope == FileLogScope.RevitOnly ? "Revit models only" : "all files") +
        (LatestVersionOnly ? ", latest version only" : ", full history") +
        (IncludeHidden ? ", incl. deleted" : "");
}
