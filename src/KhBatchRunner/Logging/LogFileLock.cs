using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace KhBatchRunner.Logging;

/// <summary>
/// Serializes appends to a single log file ACROSS PROCESSES via a named <see cref="Mutex"/>,
/// so several Revit instances running chunks of one auto-split batch can write the SAME
/// tracking/detail CSV without corrupting it or silently dropping rows (both loggers append
/// fail-soft, so a raw concurrent <c>File.AppendAllText</c> collision would lose lines).
///
/// The mutex name is derived deterministically from the file path (SHA-256, hex), so every
/// process targeting the same file computes the same name and contends on the same lock, while
/// different files never block each other. The whole read-header-then-append sequence must run
/// inside <see cref="Write"/> so the "is this a new file?" check and the append are atomic
/// against other writers (otherwise two processes both think the file is new and both emit a
/// header row).
/// </summary>
internal static class LogFileLock
{
    /// <summary>
    /// Runs <paramref name="append"/> while holding the cross-process lock for
    /// <paramref name="path"/>. Waits up to 30s for the lock; if the previous holder died
    /// mid-write (its process was killed — e.g. <c>QuitRevit</c>), ownership is granted anyway
    /// (<see cref="AbandonedMutexException"/>) since an append leaves the file consistent.
    /// Fail-soft: any locking error just runs the append unguarded rather than aborting the batch.
    /// </summary>
    public static void Write(string path, Action append)
    {
        Mutex? mutex = null;
        bool held = false;
        try
        {
            mutex = new Mutex(false, MutexNameFor(path));
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { held = true; } // prior holder was killed; we own it now
        }
        catch
        {
            // Couldn't create/acquire the mutex (name too long, permissions) — fall back to an
            // unguarded append rather than dropping the row entirely.
        }

        try
        {
            append();
        }
        finally
        {
            if (mutex != null)
            {
                try { if (held) mutex.ReleaseMutex(); } catch { /* best effort */ }
                mutex.Dispose();
            }
        }
    }

    /// <summary>
    /// A deterministic, cross-process-stable mutex name for a file path. A plain string hash is
    /// unusable here — .NET Core randomizes <c>string.GetHashCode</c> per process, so two Revit
    /// instances would derive different names and never serialize. SHA-256 over the normalized
    /// (case-insensitive) path is stable everywhere and can't contain the '\' that mutex names
    /// forbid.
    /// </summary>
    private static string MutexNameFor(string path)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(path.ToLowerInvariant());
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder("KhBatchRunner_Log_", 19 + 64);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
