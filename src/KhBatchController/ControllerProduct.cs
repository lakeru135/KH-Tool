namespace KhBatchController;

/// <summary>
/// Who the standalone says it is.
///
/// TWO VERSIONS, deliberately. <see cref="Version"/> is the controller's own — this is its first
/// release, so 0.0.1 — while <see cref="CoreEngineVersion"/> is the shared batch engine it compiles
/// in from the add-in, and moves with the add-in's releases. They are genuinely independent: the
/// controller's window layout can change without the engine moving, and an engine fix (a retry
/// round, a split-size rule) lands in both products at once. Reporting only one of them would make
/// "which build am I running?" unanswerable for whichever half actually broke.
/// </summary>
internal static class ControllerProduct
{
    public const string Name = "KH Batch Controller";

    /// <summary>The standalone's own version. First release.</summary>
    public const string Version = "0.0.1";

    /// <summary>The shared engine's version — <see cref="KhBatchRunner.Product.Version"/>, i.e.
    /// whatever add-in release these linked sources came from.</summary>
    public static string CoreEngineVersion => KhBatchRunner.Product.Version;

    public const string Publisher = "Kanghun BIM";

    public const string Description =
        "Runs the Forma batch tooling from the desktop, with Revit demoted to a background worker. " +
        "Browse Forma, build model sets, edit schedules and start a batch without opening Revit " +
        "yourself — it borrows your Autodesk sign-in from a hidden Revit and launches Revit " +
        "instances to do the model work. There is no ribbon tab here: this IS the application. " +
        "Anything that needs the model you already have open — Excel Import/Export, Correct " +
        "Workset on the active document — stays in the Revit add-in.";

    public const string Changelog =
        "KH Batch Controller (0.0.1) — first release:\n" +
        "•  Runs standalone. It signs in by borrowing the Forma token from a hidden Revit, so there\n" +
        "    is no second login and no registered app to set up.\n" +
        "•  Browse Forma, create and edit model sets, and edit script sets without starting Revit.\n" +
        "•  Batch Run and Schedule Run drive real Revit worker instances, including the parallel\n" +
        "    split, and follow them to completion with a live log.\n" +
        "•  Supervised runs: a worker that dies has its models re-run automatically, up to 4\n" +
        "    rounds — your group size, your group size again, a size that fits this PC, then one\n" +
        "    model per worker. Local-only work follows the PC's RAM/core cap; a cloud or mixed run\n" +
        "    uses one worker at a time to avoid Revit's native concurrent cloud-open crashes.\n" +
        "•  Revit is never force-closed. A completed worker receives a normal close request; a\n" +
        "    stalled worker is left open for inspection and queued work stops. Final status is a\n" +
        "    Windows Notification Center reminder with a Dismiss action, not a popup window.\n" +
        "•  Shares the batch engine, model sets, schedules and logs with the Revit add-in — the\n" +
        "    same files, so a set or schedule made in one is available in the other.\n" +
        "\n" +
        "Not here, by design: Excel Import/Export and Correct Workset on the active model. Both\n" +
        "act on the document open in front of you, which only exists inside Revit.";
}
