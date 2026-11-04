namespace KhBatchRunner.Settings;

/// <summary>
/// The names the 1.x product used on disk, in Windows and in saved schedules.
///
/// <para>2.0.0 renamed every one of them (the product became KH Batch Script Runner, working with
/// Autodesk Forma, formerly ACC). This file is the ONLY place the old spellings may live, and it
/// exists for one reason: a PC upgrading from 1.x must keep its settings, model sets and
/// schedules. <c>install\Verify-NoClientNames.ps1</c> allow-lists this file (and the installer's
/// legacy-cleanup section) for exactly that reason, so anything added here must be an old name
/// that an upgrade has to recognise, never a new one.</para>
/// </summary>
internal static class LegacyNames
{
    /// <summary>The 1.x folder under %AppData% and %LocalAppData%.</summary>
    public const string ProductFolder = "AccBatchRunner";

    /// <summary>The 1.x Windows Task Scheduler task-name prefix (followed by the job id).</summary>
    public const string TaskPrefix = "AccBatchRunner_";

    /// <summary>The 1.x name of the Initiate action. An action's name IS its schedule key, so a job
    /// saved by 1.x still carries this and must still run.</summary>
    public const string InitiateAction = "Initiate to ACC";
}
