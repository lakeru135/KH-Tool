using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Actions;

/// <summary>
/// Base class every action derives from — carries the shared defaults that used to live
/// as default interface members on <see cref="IBatchAction"/> (moved here because the
/// Revit 2024 build targets .NET Framework 4.8, whose runtime can't load them):
/// <see cref="Category"/> defaults to <see cref="ActionCategory.Check"/> so read-only
/// actions need no change; <see cref="RunDetailRows"/> defaults to wrapping
/// <see cref="Run"/>'s result as a single row; <see cref="ConfigureOutput"/> is a no-op —
/// only actions that need a sidecar file override it.
/// </summary>
public abstract class BatchActionBase : IBatchAction
{
    public abstract string Name { get; }

    public virtual ActionCategory Category => ActionCategory.Check;

    // Pure derivation of Category — non-virtual so the engine's persistence rule can't be
    // overridden out of sync with the declared category.
    public bool ModifiesModel => Category == ActionCategory.Action;

    public abstract IReadOnlyList<string> Columns { get; }

    public abstract IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc);

    public virtual IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(UIApplication uiApp, Document doc) =>
        new[] { Run(uiApp, doc) };

    public virtual void ConfigureOutput(string destinationFolder, string projectLogName) { }

    /// <summary>Set by the engine before ConfigureOutput; see <see cref="IBatchAction.RunStamp"/>.</summary>
    public DateTime? RunStamp { get; set; }

    // Only actions with a run-level artifact (e.g. a cross-model matrix) override this.
    public virtual void OnRunComplete() { }

    // Only the Dynamo script runner needs the model to be the active document.
    public virtual bool NeedsActiveDocument => false;

    // Only Batch Interop, whose instances sit on an open model long enough to collide.
    public virtual bool SerialisesCloudOpens => false;

    // No roll-up worth surfacing by default — the detail rows already carry the data.
    public virtual string PerModelNote => "";

    // ABSTRACT ON PURPOSE — this is the gate. What used to sit here was
    //
    //     public virtual bool OpensAllWorksets => false;   // "everything else reads phases,
    //                                                      //  worksets, sheets or link types,
    //                                                      //  none of which a closed workset hides"
    //
    // and that comment was wrong: an audit on 2026-08-16 found four actions collecting element
    // INSTANCES behind it. The cost of the same mistake, twice already, was five builds of Export
    // NWC writing empty files and four versions of Batch Interop calling partial models clean.
    //
    // A default answers the question on the author's behalf, silently and usually incorrectly. An
    // abstract member makes the compiler ask. install\Verify-ActionPosture.ps1 proves it still does
    // by adding an action that does not answer and requiring CS0534.
    public abstract WorksetPosture Worksets { get; }
    public abstract string WorksetPostureReason { get; }

    // Pure derivations of Worksets — non-virtual for the same reason ModifiesModel is: one source
    // of truth, so a posture and the behaviour it describes cannot quietly stop matching.
    public bool OpensAllWorksets => Worksets == WorksetPosture.OpensAll;
    public bool PlansWorksetOpen => Worksets == WorksetPosture.PlansOwn;

    public virtual ICollection<WorksetId>? SelectWorksetsToOpen(Document doc) => null;
}
