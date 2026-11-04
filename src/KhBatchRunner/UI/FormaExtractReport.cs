using KhBatchRunner.Forma;

namespace KhBatchRunner.UI;

/// <summary>
/// Maps the typed extract rows (<see cref="FormaFileLogRow"/> / <see cref="SheetPublishRow"/>) to
/// the locked CSV column order + string cells the report writer consumes. Kept separate from the
/// window so the column contract lives in one place.
/// </summary>
internal static class FormaExtractReport
{
    /// <summary>
    /// The log covers every file type, so the key column is "File Name", not "Model Name"; and the
    /// version is identified by a link somebody can click rather than the opaque
    /// <c>urn:adsk.wipprod:fs.file:vf.…</c> that "Version URN" carried. Both renamed in 1.4.5 —
    /// anything already pivoting on the old headers has to be re-pointed once.
    /// </summary>
    private static readonly string[] FileLogColumns =
    {
        "File Name", "File Type", "Version #", "URL", "Last Modified",
        "Modified By", "File Size", "Processing State", "Comment"
    };

    public static (IReadOnlyList<string> Columns, List<IReadOnlyDictionary<string, string>> Rows) FileLog(
        IReadOnlyList<FormaFileLogRow> rows)
    {
        var mapped = rows.Select(r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["File Name"] = r.FileName,
            ["File Type"] = r.FileType,
            ["Version #"] = r.VersionNumber.ToString(),
            ["URL"] = r.Url,
            ["Last Modified"] = LocalizeIso(r.LastModified),
            ["Modified By"] = r.ModifiedBy,
            ["File Size"] = FormatSize(r.StorageSize),
            ["Processing State"] = r.ProcessingState,
            ["Comment"] = r.Comment,
        }).ToList();
        return (FileLogColumns, mapped);
    }

    /// <summary>Ordered so the two columns that decide everything — the item's own lineage and the
    /// one its version claims to come from — sit next to each other and can be eyeballed.</summary>
    private static readonly string[] ScanDetailColumns =
    {
        "Model Name", "Verdict", "Revit Version", "Model Type",
        "Item URN", "Original Item URN", "Item Extension Type", "Version Extension Type",
        "Model GUID", "Project GUID", "Process State", "Extension Data Keys"
    };

    public static (IReadOnlyList<string> Columns, List<IReadOnlyDictionary<string, string>> Rows) ScanDetails(
        IReadOnlyList<FormaScanDetailRow> rows)
    {
        var mapped = rows.Select(r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["Model Name"] = r.ModelName,
            ["Verdict"] = r.Verdict,
            ["Revit Version"] = r.RevitVersion,
            ["Model Type"] = r.ModelType,
            ["Item URN"] = r.ItemUrn,
            ["Original Item URN"] = r.OriginalItemUrn,
            ["Item Extension Type"] = r.ItemExtensionType,
            ["Version Extension Type"] = r.VersionExtensionType,
            ["Model GUID"] = r.ModelGuid,
            ["Project GUID"] = r.ProjectGuid,
            ["Process State"] = r.ProcessState,
            ["Extension Data Keys"] = r.ExtensionDataKeys,
        }).ToList();
        return (ScanDetailColumns, mapped);
    }

    private static readonly string[] SheetPublishColumns =
    {
        "Model", "Sheet No.", "Is Match"
    };

    public static (IReadOnlyList<string> Columns, List<IReadOnlyDictionary<string, string>> Rows) SheetPublish(
        IReadOnlyList<KhBatchRunner.Forma.SheetPublishRow> rows)
    {
        var mapped = rows.Select(r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["Model"] = r.ModelName,
            ["Sheet No."] = r.SheetNumber,
            ["Is Match"] = r.IsMatch,
        }).ToList();
        return (SheetPublishColumns, mapped);
    }

    /// <summary>The Forma/Forge API returns UTC ISO-8601 timestamps (e.g. 2026-07-23T09:15:00.000Z).
    /// Convert to the viewer's local time in their Windows regional format; keep the raw string if it
    /// can't be parsed (so nothing is ever lost).</summary>
    private static string LocalizeIso(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        return System.DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                   out System.DateTimeOffset dto)
            ? ScheduleDisplay.Local(dto.ToLocalTime().DateTime)
            : iso;
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes is not { } b || b < 0) return "";
        if (b >= 1024L * 1024 * 1024) return $"{b / (1024d * 1024 * 1024):0.##} GB";
        if (b >= 1024L * 1024) return $"{b / (1024d * 1024):0.##} MB";
        if (b >= 1024L) return $"{b / 1024d:0.##} KB";
        return $"{b} B";
    }
}
