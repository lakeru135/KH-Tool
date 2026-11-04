using System.Windows.Forms;
using KhBatchRunner.Actions;

namespace KhBatchRunner.UI;

/// <summary>One action as a dialog needs to know it: no Revit types, so it can cross a process
/// boundary.</summary>
/// <param name="Worksets">The action's declared <see cref="WorksetPosture"/>, or NULL when the host
/// did not report one. Null is not a posture and must never be treated as one — an older broker
/// simply does not send this field, and guessing "narrow" there would let the schedule editor
/// promise an open count the run will not honour. Callers show their advisory only for a posture
/// they were actually told.</param>
public readonly record struct ActionInfo(
    string Name, ActionCategory Category, bool ModifiesModel, WorksetPosture? Worksets = null);

/// <summary>
/// The two things a shared dialog needs from whichever program is hosting it.
///
/// The dialogs in this folder are almost entirely Revit-free — <c>ScheduleRunWindow</c> is 824 lines
/// and touched Revit in exactly two places: it enumerated <see cref="ActionRegistry"/> (which hands
/// back Revit-typed <see cref="IBatchAction"/> instances) and it confirmed with a Revit
/// <c>TaskDialog</c>. Those two lines were the whole reason a standalone host could not reuse the
/// window. Routing them through here means ONE dialog implementation serves both the add-in and the
/// controller, instead of a parallel copy that drifts.
///
/// The add-in installs the Revit-backed implementations from <c>App.OnStartup</c>; the controller
/// installs its own (actions from the token broker, confirmation from a message box). The defaults
/// below are what a host gets if it installs nothing — deliberately safe rather than convenient.
/// </summary>
public static class HostServices
{
    /// <summary>
    /// The batch actions available to run. Defaults to EMPTY, not to a guess: a dialog showing no
    /// actions is obviously broken, whereas a dialog showing a stale hardcoded list looks fine and
    /// silently runs the wrong thing.
    /// </summary>
    public static Func<IReadOnlyList<ActionInfo>> ListActions { get; set; } =
        () => Array.Empty<ActionInfo>();

    /// <summary>
    /// The Revit release hosting these dialogs — <c>"2026"</c> — used to grey out a Forma model this
    /// Revit must not open (<see cref="Forma.FormaModelItem.BlockedReason"/>). The add-in sets it from
    /// <c>Application.VersionNumber</c> at startup.
    ///
    /// <para>Defaults to EMPTY, which blocks nothing, and that is the safe default here rather than
    /// the convenient one: the controller has no Revit at all, so a guess would grey out models on
    /// the say-so of a version nobody is running. Ambient rather than a constructor parameter
    /// because five separate dialogs open the model picker and a rule enforced in four of them is
    /// not a rule.</para>
    /// </summary>
    public static string RunningRevitVersion { get; set; } = "";

    /// <summary>
    /// The Autodesk account these dialogs are running under — <c>Application.LoginUserId</c>,
    /// typically an email address. Empty when nobody is signed in, when the host is not Revit, or
    /// when Revit refused to say.
    ///
    /// <para>Empty means UNKNOWN, and every caller has to treat it that way: a saved model set is
    /// never hidden or failed on the strength of a blank identity. This is the only handle the
    /// product has on "which account is this?" — it exists already in
    /// <c>Auth.TokenBrokerServer</c>, which reads it from the API thread for its WHOAMI reply, and
    /// this is the same value made available to the shared dialogs.</para>
    /// </summary>
    public static string SignedInUserId { get; set; } = "";

    /// <summary>
    /// Ask the user to confirm something consequential. Defaults to a WinForms message box, which
    /// works in any host; the add-in replaces it with Revit's own TaskDialog so an in-Revit dialog
    /// still looks like Revit. Never defaults to "yes" — these prompts guard publishing.
    /// </summary>
    public static Func<string, string, bool> Confirm { get; set; } =
        (title, message) => MessageBox.Show(message, title, MessageBoxButtons.YesNo,
                                            MessageBoxIcon.Question) == DialogResult.Yes;

    /// <summary>
    /// What the About box says. Defaults describe the Revit add-in; the standalone replaces them,
    /// because almost none of it is true there — it has no ribbon tab, it carries its own version,
    /// and "adds a tab to Revit" is the wrong first sentence for a program that runs Revit.
    /// </summary>
    public static Func<AboutFacts> About { get; set; } = () => new AboutFacts(
        Product.Name, Product.Version, Product.Publisher,
        "Ribbon tab", Product.Tab,
        // A build under test says so at the top of the one dialog people open to ask "which version
        // am I on?". Nothing else about the add-in changes — see Product.IsDevelopmentBuild. The
        // unrestricted build says so for the same reason and is even harder to spot otherwise: it
        // shares its AddInId, assembly and tab with the gated one, so this line is the only tell.
        (Product.IsDevelopmentBuild ? Product.DevelopmentBuildNote : "") +
        (Product.IsUnlimitedBuild ? Product.UnlimitedBuildNote : "") + Product.Description,
        Product.Changelog);
}

/// <summary>
/// The About box's content, supplied by the host.
///
/// <paramref name="DetailLabel"/>/<paramref name="DetailValue"/> is the one line that differs in
/// kind between hosts: the add-in names the ribbon tab it installs, the standalone names the core
/// engine version it shares with the add-in — two different versions matter there, its own and the
/// batch engine's.
/// </summary>
public readonly record struct AboutFacts(
    string Name,
    string Version,
    string Publisher,
    string DetailLabel,
    string DetailValue,
    string Description,
    string Changelog);
