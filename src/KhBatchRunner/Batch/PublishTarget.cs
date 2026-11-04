using System.Text.RegularExpressions;
using KhBatchRunner.Forma;

namespace KhBatchRunner.Batch;

/// <summary>How a prefix is applied to the source model name when publishing.</summary>
public enum PublishPrefixMode
{
    /// <summary>Prepend "&lt;prefix&gt;&lt;sep&gt;" to the whole existing name.</summary>
    Add,

    /// <summary>Replace the existing leading token (text before the first separator) with the new prefix.</summary>
    Rename
}

/// <summary>
/// The resolved destination for Batch Initiate to Forma — the account/project GUIDs and folder URN
/// that <c>Document.SaveAsCloudModel(accountId, projectId, folderId, modelName)</c> needs, plus the
/// naming rule (separator, prefix + <see cref="PublishPrefixMode"/>, suffix) and a human-readable
/// path for logs/confirmation. Built from the folder the user picks in
/// <see cref="UI.FormaFolderPickerWindow"/>.
/// </summary>
public sealed class PublishTarget
{
    public required Guid AccountGuid { get; init; }
    public required Guid ProjectGuid { get; init; }
    public required string FolderUrn { get; init; }
    public string FolderPath { get; init; } = "";

    /// <summary>Separator between prefix/name/suffix — one of ".", "_", "-".</summary>
    public string Separator { get; init; } = "-";
    public PublishPrefixMode PrefixMode { get; init; } = PublishPrefixMode.Add;
    public string Prefix { get; init; } = "";
    public string Suffix { get; init; } = "";

    /// <summary>
    /// Resolve a target from a picked folder. Throws <see cref="InvalidOperationException"/> if the
    /// hub/project ids don't yield GUIDs so the command can show a clear message instead of failing
    /// mid-publish.
    /// </summary>
    public static PublishTarget FromFolder(
        FormaEntry folder, string folderPath, string prefix, string suffix,
        string separator = "-", PublishPrefixMode prefixMode = PublishPrefixMode.Add)
    {
        if (folder.Kind != FormaKind.Folder)
            throw new InvalidOperationException("Select a folder as the publish target.");
        return FromIdentifiers(folder.HubId, folder.ProjectId, folder.Id, folderPath,
            prefix, suffix, separator, prefixMode);
    }

    /// <summary>
    /// Resolve a target from raw stored identifiers (hub id with its "b." prefix, project id,
    /// folder URN) — the persisted form (<see cref="InitiateInputSet"/> / a job snapshot) —
    /// with the same validation messages as <see cref="FromFolder"/>.
    /// </summary>
    public static PublishTarget FromIdentifiers(
        string hubId, string projectId, string folderUrn, string folderPath,
        string prefix, string suffix, string separator = "-",
        PublishPrefixMode prefixMode = PublishPrefixMode.Add)
    {
        if (!Guid.TryParse(StripB(hubId ?? ""), out Guid account))
            throw new InvalidOperationException("Could not resolve the Forma account GUID from the target hub.");
        if (!Guid.TryParse(StripB(projectId ?? ""), out Guid project))
            throw new InvalidOperationException("Could not resolve the Forma project GUID from the target folder.");
        if (string.IsNullOrEmpty(folderUrn))
            throw new InvalidOperationException("The target folder has no URN.");

        return new PublishTarget
        {
            AccountGuid = account,
            ProjectGuid = project,
            FolderUrn = folderUrn,
            FolderPath = folderPath ?? "",
            Separator = string.IsNullOrEmpty(separator) ? "-" : separator,
            PrefixMode = prefixMode,
            Prefix = prefix?.Trim() ?? "",
            Suffix = suffix?.Trim() ?? "",
        };
    }

    /// <summary>
    /// The cloud model name for a source with the given title (trailing "_detached"/".rvt"
    /// stripped), applying this target's naming rule with its chosen separator:
    ///   • <see cref="PublishPrefixMode.Add"/> prepends "&lt;prefix&gt;&lt;sep&gt;";
    ///   • <see cref="PublishPrefixMode.Rename"/> replaces the leading token (before the first
    ///     separator) with the prefix — falling back to prepend if the name has no separator;
    ///   • a suffix is appended as "&lt;sep&gt;&lt;suffix&gt;".
    /// Finally sanitized of characters Forma/OLE reject (per the batch-scripting standard).
    /// </summary>
    public string BuildPublishName(string sourceTitle)
    {
        string baseName = sourceTitle;
        if (baseName.EndsWith("_detached", StringComparison.OrdinalIgnoreCase))
            baseName = baseName[..^"_detached".Length];
        if (baseName.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
            baseName = baseName[..^4];

        string sep = string.IsNullOrEmpty(Separator) ? "-" : Separator;
        string name = baseName;

        if (!string.IsNullOrEmpty(Prefix))
        {
            if (PrefixMode == PublishPrefixMode.Rename)
            {
                int i = baseName.IndexOf(sep, StringComparison.Ordinal);
                string rest = i >= 0 ? baseName[(i + sep.Length)..] : baseName;
                name = Prefix + sep + rest;
            }
            else // Add
            {
                name = Prefix + sep + baseName;
            }
        }

        if (!string.IsNullOrEmpty(Suffix))
            name = name + sep + Suffix;

        name = Regex.Replace(name, "[\\\\/*?:\"<>|]", "").Trim();
        return string.IsNullOrEmpty(name) ? baseName : name;
    }

    private static string StripB(string id) =>
        id.StartsWith("b.", StringComparison.OrdinalIgnoreCase) ? id[2..] : id;
}
