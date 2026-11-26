namespace KhBatchRunner.UI;

/// <summary>Tiny text helpers shared by window labels.</summary>
public static class UiText
{
    /// <summary>"1 model" / "2 models": count plus the noun, pluralised with a plain "s".</summary>
    public static string Plural(int count, string noun) => count == 1 ? $"{count} {noun}" : $"{count} {noun}s";
}
