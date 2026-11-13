using System.IO;

namespace KhBatchController.Auth;

/// <summary>
/// Finds the <c>Revit.exe</c> the controller should drive.
///
/// Inside the add-in this question never came up — <see cref="System.Diagnostics.Process"/>'s own
/// main module WAS Revit (see <c>ChunkLauncher.CurrentRevitExe</c>). The controller is not Revit,
/// so the version becomes an explicit choice, and it is the same choice for both jobs: the broker
/// that supplies the token and the worker that opens models should be the same release.
/// </summary>
public static class RevitLocator
{
    /// <summary>Newest first — a machine with several releases installed gets the newest by default.</summary>
    private static readonly string[] KnownVersions = { "2027", "2026", "2025", "2024" };

    private const string OverrideEnvVar = "KHBATCH_REVIT_EXE";

    /// <summary>
    /// Resolves the Revit executable, or null with a reason. Honours
    /// <c>KHBATCH_REVIT_EXE</c> as an explicit override for a non-standard install location.
    /// </summary>
    public static string? Find(string? preferredVersion, out string error)
    {
        error = "";

        string? overridden = Environment.GetEnvironmentVariable(OverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            if (File.Exists(overridden)) return overridden;
            error = $"{OverrideEnvVar} is set to '{overridden}', which does not exist.";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredVersion))
        {
            string exact = PathFor(preferredVersion!);
            if (File.Exists(exact)) return exact;
            error = $"Revit {preferredVersion} is not installed at '{exact}'.";
            return null;
        }

        foreach (string version in KnownVersions)
        {
            string candidate = PathFor(version);
            if (File.Exists(candidate)) return candidate;
        }

        error = "No Revit installation found under C:\\Program Files\\Autodesk " +
                $"(looked for {string.Join(", ", KnownVersions)}). Set {OverrideEnvVar} to override.";
        return null;
    }

    /// <summary>The release a resolved executable path belongs to ("2026"), or "" if unrecognisable.</summary>
    public static string VersionOf(string revitExePath)
    {
        foreach (string version in KnownVersions)
        {
            if (revitExePath.Contains("Revit " + version, StringComparison.OrdinalIgnoreCase))
                return version;
        }
        return "";
    }

    private static string PathFor(string version) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     "Autodesk", "Revit " + version, "Revit.exe");
}
