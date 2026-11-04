using System.IO;

namespace KhBatchRunner.Interop;

/// <summary>
/// Whether Revit can write an NWC at all.
///
/// <para><b>Why a runtime probe and not a compile-time one.</b> <c>NavisworksExportOptions</c> ships
/// in <c>RevitAPI.dll</c>, so the code below compiles on every machine whether or not anything can
/// execute it — the actual exporter is a separate Autodesk install (<c>nwexportrevit.addin</c>,
/// <c>NavisWorks23.LcRevitExportApplication</c>) that arrives with Navisworks, not with Revit. A
/// build that compiles proves nothing about the PC it lands on, exactly as with
/// <see cref="AitLocator"/>.</para>
///
/// <para><b>Two states, not three.</b> <see cref="AitLocator"/> has an
/// <c>InstalledUnverified</c> rung because a specific AIT build once crashed a Revit and years are
/// now allow-listed one at a time. There is no equivalent evidence here, and inventing a year
/// allow-list would block Revit 2024 and 2025 users on a suspicion nobody has. Installed is
/// installed.</para>
/// </summary>
public enum NavisworksStatus
{
    /// <summary>The Revit exporter for Navisworks is installed for this Revit year.</summary>
    Installed,

    /// <summary>No exporter found — <c>Document.Export</c> would fail. Say so before the run, not
    /// once per model in the log.</summary>
    NotInstalled,
}

public static class NavisworksLocator
{
    /// <summary>The add-in manifest Navisworks installs into Revit's add-in folders. Confirmed
    /// present on the office PC in Revit 2026's journal: <c>nwexportrevit.addin</c> →
    /// <c>File exporter for Navisworks 2026</c>, v23.1.6.0, signed, loaded with NoError.</summary>
    private const string ManifestName = "nwexportrevit.addin";

    public static NavisworksStatus Check(string revitVersionNumber) =>
        IsInstalled(revitVersionNumber) ? NavisworksStatus.Installed : NavisworksStatus.NotInstalled;

    /// <summary>
    /// Looks for the exporter's manifest in the three places Revit reads add-ins from, then falls
    /// back to asking whether its assembly is already loaded in this process — which it will be, as
    /// the exporter is an <c>ExternalApplication</c> Revit starts at launch. The manifest check
    /// alone would miss a non-standard install; the assembly check alone would miss nothing but
    /// depends on Revit having got that far.
    /// </summary>
    public static bool IsInstalled(string revitVersionNumber)
    {
        try
        {
            foreach (string dir in ManifestDirs(revitVersionNumber))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                if (File.Exists(Path.Combine(dir, ManifestName))) return true;
            }
        }
        catch { /* an unreadable add-in folder is not evidence either way — fall through */ }

        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = asm.GetName().Name ?? "";
                if (name.StartsWith("NavisWorks", StringComparison.OrdinalIgnoreCase) ||
                    name.IndexOf("LcRevitExport", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        catch { /* best effort */ }

        return false;
    }

    private static IEnumerable<string> ManifestDirs(string year)
    {
        // All-users, then Revit's own program folder, then per-user — the same order Revit itself
        // loads them in.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Autodesk", "Revit", "Addins", year);
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Autodesk", "Revit " + year, "AddIns", "NavisworksExporter");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", year);
    }
}
