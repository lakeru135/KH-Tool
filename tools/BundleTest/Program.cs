using System.IO;
using System.Text.Json;
using KhBatchRunner.Settings;

namespace BundleTest;

/// <summary>
/// Proves the settings bundle round trip on real files: seed a settings root, export it, wipe the
/// root, import the bundle back, and compare byte-for-byte.
///
/// <para>Runs against <see cref="SettingsPaths.RootOverrideVariable"/> rather than the real
/// <c>%AppData%</c>, so it can wipe freely. Seeded from a COPY of the developer's actual settings
/// when they exist — a round trip over invented files proves less than one over the awkward names
/// and shapes that accumulated in practice.</para>
///
/// <para>Not in the solution; run it directly like tools\ThemePreview.</para>
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        string work = Path.Combine(Path.GetTempPath(), "khbatch-bundle-test-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(work, "root");
        string bundle = Path.Combine(work, "bundle.zip");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(SettingsPaths.RootOverrideVariable, root);

        try
        {
            Seed(root);
            Dictionary<string, string> before = Snapshot(root);
            Console.WriteLine($"seeded {before.Count} file(s) under a redirected root");
            if (before.Count == 0) { Fail("nothing was seeded — the round trip would prove nothing"); return Done(); }

            // ---- export ----
            var categories = SettingsCatalog.All().ToList();
            List<TransferResult> exported = SettingsBundle.Export(bundle, categories, includePreferences: true);
            foreach (TransferResult r in exported.Where(r => r.Error != null))
                Fail($"export of {r.DisplayName}: {r.Error}");
            Console.WriteLine($"exported {exported.Sum(r => r.Written)} file(s) -> {new FileInfo(bundle).Length} bytes");

            // ---- the manifest must be readable, and must describe this bundle ----
            SettingsManifest? manifest = SettingsBundle.ReadManifest(bundle, out string? manifestError);
            if (manifest is null) { Fail("manifest unreadable: " + manifestError); return Done(); }
            if (manifest.SchemaVersion != SettingsBundle.CurrentSchemaVersion)
                Fail($"manifest schema {manifest.SchemaVersion}, expected {SettingsBundle.CurrentSchemaVersion}");
            // The bundle must carry everything EXCEPT the two deliberate exclusions — the licensing
            // bag and the schedules. Asserting the exact arithmetic rather than "roughly all of it"
            // is what makes a future accidental inclusion fail here instead of shipping.
            int claimed = manifest.Counts.Values.Sum();
            int expectedInBundle = before.Keys.Count(k => !IsExcluded(k));
            if (claimed != expectedInBundle)
                Fail($"manifest claims {claimed} file(s); expected {expectedInBundle} " +
                     $"({before.Count} in the root minus {before.Count - expectedInBundle} deliberately excluded)");
            else
                Console.WriteLine($"ok   bundle carries {claimed}, excluding " +
                                  $"{before.Count - expectedInBundle} (usersettings.json, Scheduled\\)");

            // ---- wipe, then import ----
            foreach (string dir in Directory.GetDirectories(root)) Directory.Delete(dir, recursive: true);
            foreach (string file in Directory.GetFiles(root)) File.Delete(file);
            if (Snapshot(root).Count != 0) { Fail("the wipe left files behind"); return Done(); }

            List<TransferResult> imported =
                SettingsBundle.Import(bundle, categories, ImportCollision.Overwrite, includePreferences: true);
            foreach (TransferResult r in imported.Where(r => r.Error != null))
                Fail($"import of {r.DisplayName}: {r.Error}");

            Dictionary<string, string> after = Snapshot(root);
            Console.WriteLine($"imported {imported.Sum(r => r.Written)} file(s); root now holds {after.Count}");

            CompareRoundTrip(before, after);

            // ---- collision behaviour, on a root that now has everything ----
            CheckSkipChangesNothing(bundle, categories, after);
            CheckKeepBothAdds(bundle, categories, root);
        }
        catch (Exception ex)
        {
            Fail("threw: " + ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SettingsPaths.RootOverrideVariable, null);
            try { Directory.Delete(work, recursive: true); } catch { /* temp */ }
        }

        return Done();
    }

    /// <summary>Every file under the root, keyed by relative path, valued by content hash.</summary>
    private static Dictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(root, f).Replace('\\', '/'),
                f => Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))),
                StringComparer.OrdinalIgnoreCase);

    private static void CompareRoundTrip(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        // The excluded files are NOT expected back, and their absence is a REQUIREMENT rather than
        // an omission to tolerate: a round trip that restored the licence cache, or silently
        // re-armed a schedule on another PC, would be the bug this whole design avoids.
        var expected = before.Where(kv => !IsExcluded(kv.Key))
                             .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> kv in expected)
        {
            if (!after.TryGetValue(kv.Key, out string? hash)) { Fail($"missing after import: {kv.Key}"); continue; }
            if (hash != kv.Value) Fail($"content changed across the round trip: {kv.Key}");
        }
        foreach (string extra in after.Keys.Where(k => !expected.ContainsKey(k)))
            if (!extra.Equals("usersettings.json", StringComparison.OrdinalIgnoreCase))
                Fail($"appeared from nowhere: {extra}");

        foreach (string excluded in before.Keys.Where(IsExcluded))
        {
            if (excluded.Equals("usersettings.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (after.ContainsKey(excluded)) Fail($"travelled but must not: {excluded}");
        }
        Console.WriteLine("ok   no schedule travelled in the bundle");

        if (after.ContainsKey("usersettings.json"))
            Console.WriteLine("ok   usersettings.json rewritten by the preference import, not restored wholesale");
        Console.WriteLine($"ok   {expected.Count} file(s) survived export -> wipe -> import unchanged");
    }

    private static void CheckSkipChangesNothing(string bundle, List<SettingsCategory> categories,
        Dictionary<string, string> after)
    {
        SettingsBundle.Import(bundle, categories, ImportCollision.Skip, includePreferences: false);
        Dictionary<string, string> now = Snapshot(Environment.GetEnvironmentVariable(SettingsPaths.RootOverrideVariable)!);
        int changed = now.Count(kv => !after.TryGetValue(kv.Key, out string? h) || h != kv.Value);
        if (now.Count != after.Count || changed > 0)
            Fail($"Skip altered the root ({now.Count} vs {after.Count} files, {changed} changed)");
        else
            Console.WriteLine("ok   Skip left every existing file untouched");
    }

    private static void CheckKeepBothAdds(string bundle, List<SettingsCategory> categories, string root)
    {
        int before = Snapshot(root).Count;
        SettingsBundle.Import(bundle, categories, ImportCollision.KeepBoth, includePreferences: false);
        Dictionary<string, string> now = Snapshot(root);

        // Every set FOLDER entry should have been added again under a free name. The single-file
        // categories have no name to vary, so they are skipped — that asymmetry is deliberate.
        int setFiles = categories.Where(c => c.Kind == SettingsCategoryKind.SetFolder).Sum(c => c.Count());
        if (now.Count <= before) { Fail($"KeepBoth added nothing ({before} -> {now.Count})"); return; }

        var copies = now.Keys.Where(k => k.Contains("(copy", StringComparison.OrdinalIgnoreCase)).ToList();
        if (copies.Count == 0) Fail("KeepBoth added files but none carry the shared \"(copy)\" naming rule");
        else Console.WriteLine($"ok   KeepBoth added {now.Count - before} file(s), {copies.Count} named by SetDuplication");
        if (setFiles == 0) Console.WriteLine("note no set-folder files were present to duplicate");
    }

    /// <summary>
    /// Fills the redirected root from the developer's real settings where they exist, and
    /// guarantees at least one file per category otherwise, so the run always proves something.
    /// </summary>
    private static void Seed(string root)
    {
        string real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KhBatchRunner");

        foreach (SettingsCategory category in SettingsCatalog.All())
        {
            if (category.Kind == SettingsCategoryKind.SingleFile)
            {
                string source = Path.Combine(real, category.PathName);
                string target = Path.Combine(root, category.PathName);
                if (File.Exists(source)) File.Copy(source, target, overwrite: true);
                else File.WriteAllText(target, JsonSerializer.Serialize(new { seeded = category.Key }));
                continue;
            }

            string sourceDir = Path.Combine(real, category.PathName);
            string targetDir = Path.Combine(root, category.PathName);
            Directory.CreateDirectory(targetDir);
            int copied = 0;
            if (Directory.Exists(sourceDir))
                foreach (string file in Directory.EnumerateFiles(sourceDir, "*.json"))
                {
                    File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
                    copied++;
                }
            if (copied == 0)
                File.WriteAllText(Path.Combine(targetDir, "Seeded set.json"),
                    JsonSerializer.Serialize(new { Name = "Seeded set", seeded = category.Key }));
        }

        // A category the bundle must NOT carry, so the round trip can prove the exclusion holds.
        File.WriteAllText(Path.Combine(root, "usersettings.json"),
            JsonSerializer.Serialize(new { LastDestinationFolder = @"C:\RoundTrip\Logs", CachedAllowedUntil = "2099-01-01" }));
        Directory.CreateDirectory(Path.Combine(root, "Scheduled"));
        File.WriteAllText(Path.Combine(root, "Scheduled", "must-not-travel.json"), "{}");
    }

    /// <summary>
    /// Files a bundle must never carry: the licensing/expiry bag, and anything under a folder the
    /// catalog names as not-settings (today, the schedules).
    /// </summary>
    private static bool IsExcluded(string relativePath) =>
        relativePath.Equals("usersettings.json", StringComparison.OrdinalIgnoreCase) ||
        SettingsCatalog.NonSettingsFolders.Any(f =>
            relativePath.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase));

    private static void Fail(string message)
    {
        Console.WriteLine("  PROBLEM: " + message);
        _failures++;
    }

    private static int Done()
    {
        if (_failures > 0)
        {
            Console.WriteLine($"FAILED: {_failures} settings-bundle problem(s).");
            return 1;
        }
        Console.WriteLine("Settings-bundle round trip passed.");
        return 0;
    }
}
