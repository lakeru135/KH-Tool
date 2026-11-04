#if NETFRAMEWORK
using System.Collections.Generic;

namespace KhBatchRunner.Compat;

/// <summary>
/// net48 (Revit 2024) only. Small BCL gaps that .NET Core/8 has but .NET Framework 4.8
/// doesn't — provided as source so call sites stay identical across both targets. Compiled
/// out on net8 (2025/2026), where the framework already supplies these.
/// </summary>
internal static class NetFxShims
{
    /// <summary>
    /// <c>KeyValuePair&lt;,&gt;.Deconstruct</c> was added in .NET Core 2.0 / netstandard2.1;
    /// net48 lacks it, so <c>foreach ((k, v) in dictionary)</c> won't compile there without
    /// this extension. On net8 the built-in instance method is preferred, so this must NOT be
    /// compiled there or it would make the call ambiguous — hence the <c>#if NETFRAMEWORK</c>.
    /// </summary>
    public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
    {
        key = pair.Key;
        value = pair.Value;
    }
}
#endif
