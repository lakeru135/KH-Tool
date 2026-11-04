namespace KhBatchRunner.UI;

/// <summary>
/// The naming rule every "Duplicate" button in the product shares.
///
/// <para><b>Why this is one file (1.4.7).</b> The rule existed twice — byte-identical private copies
/// in <c>FormaBrowserWindow</c> and <c>EditScriptSetWindow</c> — and 1.4.7 adds a Duplicate button to
/// nine more windows. Eleven copies of a naming rule is how "(copy)" and "(2)" end up meaning
/// different things in different dialogs, which is exactly the drift
/// <see cref="ScheduleJobListPanel"/> was created to undo.</para>
/// </summary>
internal static class SetDuplication
{
    /// <summary>
    /// "&lt;name&gt; (copy)", then "(copy 2)", "(copy 3)"… until it does not collide with
    /// <paramref name="existingNames"/>. Case-insensitive, because the stores are file-backed and
    /// Windows file names are.
    /// </summary>
    public static string UniqueName(string baseName, IEnumerable<string> existingNames)
    {
        var existing = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string candidate = baseName + " (copy)";
        int n = 2;
        while (existing.Contains(candidate)) candidate = $"{baseName} (copy {n++})";
        return candidate;
    }
}
