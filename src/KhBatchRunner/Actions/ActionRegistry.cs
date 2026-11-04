using System.Runtime.CompilerServices;

namespace KhBatchRunner.Actions;

/// <summary>
/// The built-in C# actions available to Multi Model Check and Schedule Run, looked up
/// by <see cref="IBatchAction.Name"/>. Add new actions here to expose them everywhere.
/// </summary>
public static class ActionRegistry
{
    public static IReadOnlyList<IBatchAction> All()
    {
        try
        {
            return AllCore();
        }
        catch
        {
            // Every action class implements IBatchAction, whose Run/RunDetailRows signatures
            // reference Autodesk.Revit.UI.UIApplication — constructing them (even just to
            // list their Name in a checklist) can fail to resolve that type with no real
            // Revit host in the process (e.g. tools/ThemePreview rendering a dialog
            // standalone). Degrade to an empty list rather than taking the whole caller's
            // window down — that's a rendering preview, not a real batch run.
            return Array.Empty<IBatchAction>();
        }
    }

    // Isolated behind its own (never-inlined) method so a type-load failure surfaces at this
    // call site — catchable by All()'s try/catch above — instead of failing to JIT-compile
    // All() itself before that try/catch could ever run.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IBatchAction[] AllCore()
    {
#if EDITION_DRW
        // DRW Helper's Batch Run and Schedule Run offer only DRW's own tools: one action per saved
        // Enlarge Room / Enlarge Stair / Grid Dimension / Stair Annotation preset, read from disk
        // on every call. Tools that need someone to pick things (Sync View, interactive Enlarge)
        // are not here. See Drw\DrwActions.cs and docs\drw-helper-plan.md.
        return Drw.DrwActions.All().ToArray();
#else
        var all = new List<IBatchAction>
        {
            new GetSiteLocationPbpAction(),
            new GetPhaseAndWorksetAction(),
            new GetDesignOptionAction(),
            new GetRvtLinksWorksetAction(),
            new DrawingProgressAction(),
            new TypeMarkTextNoteAction(),
            new PurgeImportCadAction(),
            new IsolateWarningElementsAction(),
#if DEBUG
            new SeedCadImportAction(),
            new SeedWarningAction(),
#endif
        };
        // Release-compiled (the publish test loop drives the INSTALLED build headlessly) but
        // env-gated so it never shows up in a user's action list — see SeedParamTouchAction.
        if (Environment.GetEnvironmentVariable("KHBATCH_DEV_ACTIONS") == "1")
            all.Add(new SeedParamTouchAction());
        return all.ToArray();
#endif
    }

    /// <summary>
    /// Create an action by name; falls back to the first action if unknown. Throws instead of
    /// indexing blindly — <see cref="All"/> can legitimately return an empty list outside a
    /// real Revit host (see its own doc comment), and indexing an empty list is exactly the
    /// bug this guards against.
    /// </summary>
    public static IBatchAction Create(string name)
    {
        IReadOnlyList<IBatchAction> all = All();
#if EDITION_DRW
        // DRW's action names are "<tool> — <preset>", and presets can be renamed or deleted after a
        // schedule saved one. Falling back to the first action would run a DIFFERENT preset and
        // report success, so a missing name stops that item instead. (Seen on 2026-09-22: a job
        // whose names lost their em dashes ran the first preset six times, all "OK".)
        // A name saved before a tool's rename ("Grid Dimension — X", before 0.2.6) maps to its new one.
        string current = ActionNames.Canonical(name);
        return all.FirstOrDefault(a => a.Name == current)
               ?? throw new InvalidOperationException(
                   $"No DRW preset action named \"{name}\" — the preset was renamed or deleted since this run was saved.");
#else
        return all.FirstOrDefault(a => a.Name == name)
               ?? all.FirstOrDefault()
               ?? throw new InvalidOperationException("No built-in actions available — is this running inside Revit?");
#endif
    }
}
