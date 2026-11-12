namespace KhBatchRunner;

/// <summary>
/// Single source of truth for the add-in's identity, shown in the About dialog. Keep
/// <see cref="Version"/> in step with the <c>AppVersion</c> in BOTH installers
/// (<c>install\KhBatchRunner.iss</c> and <c>install\DrwHelper-2026.iss</c>), and bump
/// <see cref="BuiltInExpiryUtc"/> alongside it, when cutting an installer.
/// </summary>
internal static class Product
{
#if EDITION_DRW
    // "DRW Helper" — the drawing-production ribbon: enlarged plans/sections/elevations for rooms and
    // stairs, grid and stair annotation, used mostly in the ACTIVE model. Built from the same source
    // via -p:Edition=DRW so it reuses Model Sets, Batch Run and theming; its own assembly, manifest
    // (AddInId) and installer make it a product distinct from the batch runner.
    public const string Name = "DRW Helper";
    public const string Tab = "DRW Helper";
    public const string ScheduleOwner = "DRW";

    public const string Description =
        "Drawing-production tools for Revit: create enlarged plans, sections and elevations for " +
        "rooms and stairs, dimension grids and annotate stairs in the active model, or batch-run " +
        "the tools that need no picking over saved model sets.";
#else
    // Display only — dialog titles, About, the settings bundle's "from" line and a scheduled task's
    // description; nothing reads it back.
    public const string Name = "KH-Tool";
    public const string Tab = "KH-Tool";
    public const string ScheduleOwner = "Full";

    public const string Description =
        "Works with Autodesk Forma (formerly ACC) and BIM 360 projects. Browse Forma, save model " +
        "and folder sets, and batch-run checks, write-actions and Forma operations (file log, " +
        "initiate to Forma, cut model, internal relink, sheet-publish check) across many cloud or " +
        "local Revit models — interactively or on a schedule.\n\n" +
        "Autodesk, Revit, Forma, ACC, BIM 360 and Navisworks are trademarks of Autodesk, Inc. " +
        "This project is not affiliated with or endorsed by Autodesk.";
#endif

    // One version line for every edition: the public repository starts at 2.0.0.
    public const string Version = "2.1.0.0";
    public const string Publisher = "Kanghun BIM";

    /// <summary>
    /// Whether this build is still under test rather than blessed for production. There is no
    /// separate Dev edition any more — one add-in, one ribbon tab, one set of scheduled jobs — so
    /// this flag is the only thing that distinguishes "the build we are trying" from "the build we
    /// stand behind", and it says so in About where someone checking a version will look.
    ///
    /// It changes exactly two things and no more: the About note, and whether the Diagnostic Report
    /// button appears on the Setting panel (<see cref="App"/>). It does NOT touch the assembly, the
    /// AddInId, the tab, the schedule owner or the notification identity — a marker that alters
    /// real behaviour is a second edition by another name, and testing a build that behaves
    /// differently from the one that ships proves very little.
    ///
    /// Note the diagnostics consequence: with this <c>false</c>, NO edition shows the button —
    /// including the unrestricted build. The collector itself still ships (Start-menu "Collect
    /// diagnostics report" and <c>install\Collect-Diagnostics.cmd</c>), so a report can still be
    /// produced; it is just no longer one click away inside Revit. Flip to <c>true</c> when cutting
    /// a build you intend to gather reports from, and <c>false</c> to mark a release production.
    /// </summary>
    public const bool IsDevelopmentBuild = true;

    /// <summary>Prefixed to the About description while <see cref="IsDevelopmentBuild"/> holds.</summary>
    public const string DevelopmentBuildNote =
        "DEVELOPMENT BUILD — under test, not yet released for production use. Report anything odd " +
        "with Setting → Diagnostic Report.\n\n";

    /// <summary>
    /// Whether the expiry gate is compiled out of this build (<c>-p:Edition=FullUnlimited</c>).
    /// The unrestricted build is otherwise byte-for-byte the same product — same AddInId, same
    /// assembly, same ribbon tab, same schedule owner — so About is the only place inside Revit
    /// that can tell you which of the two you are running. Say so there.
    /// </summary>
#if NO_EXPIRY_GATE
    public const bool IsUnlimitedBuild = true;
#else
    public const bool IsUnlimitedBuild = false;
#endif

    /// <summary>Prefixed to the About description while <see cref="IsUnlimitedBuild"/> holds.</summary>
    public const string UnlimitedBuildNote =
        "UNLIMITED BUILD — no expiry check, so this copy never has to be updated. Internal use " +
        "only; do not distribute.\n\n";

    /// <summary>
    /// Fallback build-validity date, used only when the remote manifest says nothing usable — an
    /// offline PC stops here. Set it to 90 DAYS FROM THE BUILD each release (not the 18 months it
    /// once was): it is the backstop for a machine that never reaches GitHub, and the whole point
    /// is that such a machine has to come back for a new installer within the quarter.
    ///
    /// The date below is the default; <c>-p:ExpiryUtc=yyyy-MM-dd</c> overrides it at build time via
    /// assembly metadata (see KhBatchRunner.csproj). That override exists so an already-expired
    /// build can be produced for testing without ever committing a past date to source.
    /// </summary>
    public static readonly DateTime BuiltInExpiryUtc = ResolveBuiltInExpiryUtc();

    private static DateTime ResolveBuiltInExpiryUtc()
    {
        var fallback = new DateTime(2027, 2, 10, 0, 0, 0, DateTimeKind.Utc);   // 2.1.0.0: 90 days from 2026-11-12
        try
        {
            foreach (var attr in typeof(Product).Assembly
                         .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
            {
                if (attr is System.Reflection.AssemblyMetadataAttribute meta
                    && meta.Key == "BuiltInExpiryUtc"
                    && DateTime.TryParseExact(meta.Value, "yyyy-MM-dd",
                           System.Globalization.CultureInfo.InvariantCulture,
                           System.Globalization.DateTimeStyles.None, out DateTime stamped))
                    return DateTime.SpecifyKind(stamped, DateTimeKind.Utc);
            }
        }
        catch { /* a malformed stamp must never stop the add-in loading */ }
        return fallback;
    }

    /// <summary>Short "what's new" list shown on the About dialog. Keep the newest at the top.</summary>
#if EDITION_DRW
    public const string Changelog =
        "DRW Helper (2.1.0):\n" +
        "• Version kept in step with KH-Tool 2.1.0; the DRW tools are unchanged.\n\n" +
        "DRW Helper (2.0.0) — first public release:\n" +
        "• Versions now follow the shared 2.x line of the public KH-Tool repository.\n" +
        "• Create: Enlarge Room, Enlarge Stair, Enlarge Door & Window.\n" +
        "• Annotation: Datum Dimension, Match Datums, Single Font, Stair Annotation, Preset Tagger, " +
        "Wall Mat Tag.\n" +
        "• Setting: Model Set, Batch Run, Sync View.";
#else
    public const string Changelog =
        "KH-Tool (2.1.0):\n" +
        "• NWD Helper: new Export IFC + Data — exports IFC with element and room data as CSV. Runs " +
        "from its own button, Batch Run or Schedule Run. The .frag conversion for web viewers needs " +
        "the IFC-to-FRAG converter, which the installer does not include yet.\n" +
        "• Internal code cleanup; no tool behaves differently.\n\n" +
        "KH-Tool (2.0.0) — first public release:\n" +
        "• Forma is now Autodesk Forma: every panel, window and message says Forma (formerly ACC).\n" +
        "• Cut Model and Internal Relink moved onto the Forma Batch Action panel.\n" +
        "• Correct Workset moved onto the Model Batch Action panel (formerly Multi Model Action).\n" +
        "• Schedule Run now schedules Internal Relink and Correct Workset too — one schedule window " +
        "for every action.\n" +
        "• Upgrading from 1.x: settings, model sets and scheduled tasks are carried over on first " +
        "start; the 1.x settings folder is kept untouched as a backup.\n" +
        "• Batch Interop: one summary workbook per run; Rebuild Report can rebuild it.\n" +
        "• Non-workshared cloud models open plain (no detach), decided per model.";
#endif
}
