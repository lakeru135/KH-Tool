using System.IO;
using KhBatchRunner.Schedule;

namespace KhBatchRunner.Settings;

/// <summary>
/// Carries a 1.x install's state into 2.0.0, once, without touching the original.
///
/// <para><b>Settings.</b> <see cref="CopySettingsOnce"/> copies %AppData%\&lt;1.x folder&gt; and
/// %LocalAppData%\&lt;1.x folder&gt; into the 2.0.0 roots (<see cref="SettingsPaths.Root"/> /
/// <see cref="SettingsPaths.LocalRoot"/>), never overwriting a file that already exists there, and
/// writes <see cref="MarkerFile"/> so it never runs again. The 1.x folders are left exactly as they
/// were: they are the user's backup, and deleting them is the user's decision.</para>
///
/// <para><b>Schedules.</b> The copied jobs carry 1.x registration markers, so the existing
/// <see cref="TaskSchedulerGateway.MigrateLegacyRegistrations"/> pass re-registers each one under
/// its 2.0.0 task name on the first interactive start. <see cref="RemoveLegacyTasks"/> then deletes
/// the 1.x task of every job whose 2.0.0 task is in place — never before, so a failed
/// re-registration leaves the old task visible rather than leaving the job with no task at all.</para>
/// </summary>
internal static class LegacyMigration
{
    public const string MarkerFile = "migrated-from-1.x.txt";

    /// <summary>Run first in OnStartup, before anything reads a store. Never throws.</summary>
    public static void CopySettingsOnce()
    {
        // The test seam moves Root somewhere else entirely; migrating into it would be wrong.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SettingsPaths.RootOverrideVariable)))
            return;
        CopyOnce(Environment.SpecialFolder.ApplicationData, SettingsPaths.Root);
        CopyOnce(Environment.SpecialFolder.LocalApplicationData, SettingsPaths.LocalRoot);
    }

    private static void CopyOnce(Environment.SpecialFolder baseFolder, string newRoot)
    {
        try
        {
            string marker = Path.Combine(newRoot, MarkerFile);
            if (File.Exists(marker)) return;

            string oldRoot = Path.Combine(Environment.GetFolderPath(baseFolder), LegacyNames.ProductFolder);
            int copied = 0;
            if (Directory.Exists(oldRoot))
                copied = CopyTree(oldRoot, newRoot);

            File.WriteAllText(marker,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} — {copied} file(s) copied from {oldRoot}.\r\n" +
                "The original folder was left untouched as a backup; delete it once 2.x works for you.\r\n");
            Diagnostics.RunTrace.Mark($"1.x migration: {copied} file(s) from {oldRoot}");
        }
        catch (Exception ex)
        {
            // Not fatal: the user can still copy the folder by hand. Leave no marker so the next
            // start tries again.
            try { Diagnostics.RunTrace.Mark("1.x migration failed: " + ex.Message); } catch { }
        }
    }

    private static int CopyTree(string from, string to)
    {
        int n = 0;
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            string dest = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(dest)) continue;   // a 2.x file always wins
            try { File.Copy(file, dest); n++; }
            catch { /* a locked log is not worth failing the rest for */ }
        }
        foreach (string dir in Directory.EnumerateDirectories(from))
            n += CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
        return n;
    }

    /// <summary>
    /// Deletes the 1.x task of each job whose 2.0.0 registration is current. Interactive start
    /// only, after <see cref="TaskSchedulerGateway.MigrateLegacyRegistrations"/>. Never throws.
    /// </summary>
    public static void RemoveLegacyTasks()
    {
        try
        {
            HashSet<string> registered = new(TaskSchedulerGateway.RegisteredTaskNames(LegacyNames.TaskPrefix),
                StringComparer.OrdinalIgnoreCase);
            if (registered.Count == 0) return;

            foreach (ScheduledJob job in ScheduleStore.LoadAll())
            {
                string legacy = LegacyNames.TaskPrefix + job.Id;
                if (!registered.Contains(legacy)) continue;
                if (TaskSchedulerGateway.NeedsRegistrationUpgrade(
                        TaskSchedulerGateway.ExistingRegistrationText(job.Id)))
                    continue;   // 2.0.0 task not in place yet — keep the old one
                (bool ok, string msg) = TaskSchedulerGateway.DeleteTaskByName(legacy);
                Diagnostics.RunTrace.Mark($"1.x task {legacy}: {(ok ? "removed" : "kept — " + msg)}");
            }
        }
        catch (Exception ex)
        {
            try { Diagnostics.RunTrace.Mark("1.x task cleanup failed: " + ex.Message); } catch { }
        }
    }
}
