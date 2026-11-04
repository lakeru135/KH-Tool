using KhBatchRunner.Forma;
using KhBatchRunner.UI;

namespace KhBatchRunner.Presets;

/// <summary>How a saved model set stands against the Autodesk account currently signed in.</summary>
public enum AccessVerdict
{
    /// <summary>Nothing in the set names a project this account cannot reach.</summary>
    Accessible,

    /// <summary>Some of the set is reachable and some is not — a set spanning two hubs, typically.</summary>
    Partial,

    /// <summary>Every project the set names is outside this account.</summary>
    NoAccess,

    /// <summary>
    /// Not answerable. Either the set carries no Forma identity to test (every model-list set saved
    /// before 1.5.0) or the account's own project list could not be read (offline, not signed in,
    /// Forma down).
    ///
    /// <para>Unknown is NOT a soft "no". It must never hide a set or fail a run: the difference
    /// between "this account cannot open it" and "we could not tell" is the whole reason this is a
    /// four-valued answer and not a bool.</para>
    /// </summary>
    Unknown
}

/// <summary>
/// What the signed-in Autodesk account can actually reach: the hubs and Data Management projects
/// its token enumerates. Read once and cached, because it costs one REST call per hub.
/// </summary>
public sealed class AccessSnapshot
{
    /// <summary>False when the list could not be read at all. Every verdict is then Unknown.</summary>
    public bool Reachable { get; init; }

    /// <summary>The account this snapshot describes, so a stale one can be recognised as stale.</summary>
    public string SignedInUser { get; init; } = "";

    /// <summary>Hub ids (<c>b.</c>-prefixed) this account can enumerate.</summary>
    public HashSet<string> HubIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Data Management project ids (<c>b.</c>-prefixed) this account can enumerate.</summary>
    public HashSet<string> ProjectIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why it is unreachable, for a status line. Empty when it is reachable.</summary>
    public string Error { get; init; } = "";

    /// <summary>
    /// The Autodesk account the LAST session ran under, when it differs from this one — otherwise
    /// empty. Set once, on the snapshot that first noticed the change.
    ///
    /// <para>This is the fact nothing in the product used to have. Schedules are Windows tasks
    /// keyed to the Windows user, so they keep firing across an Autodesk account change with no
    /// signal of any kind; carrying the previous account here lets both a dialog and a run log say
    /// "this changed" instead of only "this failed".</para>
    /// </summary>
    public string PreviousUser { get; init; } = "";

    /// <summary>True when this snapshot is the one that noticed the account change.</summary>
    public bool AccountChanged => PreviousUser.Length > 0;

    /// <summary>When it was taken — a snapshot is a fact about a moment, not a standing truth.</summary>
    public DateTime TakenAt { get; init; } = DateTime.Now;

    internal static AccessSnapshot Unreachable(string user, string error) =>
        new() { Reachable = false, SignedInUser = user, Error = error };
}

/// <summary>
/// Answers "can the account signed in right now open this model set?".
///
/// <para><b>Why this is needed at all.</b> A schedule is a Windows task keyed to the WINDOWS user,
/// so signing Revit into a different Autodesk account changes nothing about whether a job fires —
/// only about whether its models can be opened. Before 1.5.0 nothing recorded which account a
/// saved set belonged to, so the product could not say which sets a new account had broken; the
/// first sign was a nightly run whose every model failed to open.</para>
///
/// <para><b>The test is by PROJECT, not by model.</b> Forma grants access per project, and a set's
/// models carry <see cref="CloudModel.DmProjectId"/> (captured by the crawler) while folder-backed
/// sets carry <see cref="FolderRef.ProjectId"/>. The C4R <see cref="CloudModel.ProjectGuid"/> is
/// deliberately NOT used: it is a different identifier with no relation to the Data Management
/// project id, and matching on it would answer confidently and wrongly.</para>
///
/// <para><b>Never blocks and never guesses.</b> Missing identity is <see cref="AccessVerdict.Unknown"/>,
/// an unreadable account is <see cref="AccessVerdict.Unknown"/>, and callers are required to treat
/// Unknown as "show it and run it".</para>
/// </summary>
public static class AccountAccess
{
    private static readonly object Gate = new();
    private static AccessSnapshot? _cached;

    /// <summary>The snapshot already taken this session, or null if none has been.</summary>
    public static AccessSnapshot? Cached
    {
        get { lock (Gate) return _cached; }
    }

    /// <summary>
    /// Throws the cached snapshot away. Called when the signed-in account changes — the whole
    /// point of the cache is that the answer is stable, and the one event that makes it unstable
    /// is the one this product is trying to notice.
    /// </summary>
    public static void Invalidate()
    {
        lock (Gate) _cached = null;
    }

    /// <summary>
    /// Installs a snapshot without asking Forma for one. <b>Test seam only</b> — nothing in the
    /// shipping product calls it, in the same spirit as
    /// <see cref="Settings.SettingsPaths.RootOverrideVariable"/>.
    ///
    /// <para>It exists because the interesting code path is unreachable outside Revit. With no Forma
    /// token every snapshot comes back <see cref="AccessSnapshot.Reachable"/> = false, so the
    /// window gates could never exercise what a window does once a GOOD snapshot is already
    /// cached — which is precisely the path that shipped a constructor-time crash in 1.5.0.</para>
    /// </summary>
    public static void Seed(AccessSnapshot? snapshot)
    {
        lock (Gate) _cached = snapshot;
    }

    /// <summary>
    /// Reads the account's hubs and projects. Blocking and REST-bound (one call for the hub list,
    /// one per hub) — call it off the UI thread; <see cref="UI.FormaTreeController"/> already shows
    /// what synchronous Forma calls on the UI thread look like from the outside.
    ///
    /// <para>Never throws. A failure becomes an unreachable snapshot, which reads as Unknown
    /// everywhere and changes no behaviour.</para>
    /// </summary>
    public static AccessSnapshot Refresh(DataManagementClient client)
    {
        string user = HostServices.SignedInUserId;
        AccessSnapshot snapshot;
        try
        {
            if (!client.HasToken())
            {
                snapshot = AccessSnapshot.Unreachable(user, "Not signed in to Autodesk in Revit.");
            }
            else
            {
                var hubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FormaEntry hub in client.GetHubs())
                {
                    if (!string.IsNullOrWhiteSpace(hub.Id)) hubs.Add(hub.Id);
                    // One hub failing must not lose the hubs that answered: a partial list is still
                    // a true statement about the projects it names, and every project it omits
                    // simply stays Unknown rather than becoming a false "no access".
                    try
                    {
                        foreach (FormaEntry project in client.GetProjects(hub))
                            if (!string.IsNullOrWhiteSpace(project.ProjectId))
                                projects.Add(project.ProjectId);
                    }
                    catch { /* see above */ }
                }

                snapshot = hubs.Count == 0
                    ? AccessSnapshot.Unreachable(user, "This account reports no Forma hubs.")
                    : new AccessSnapshot
                    {
                        Reachable = true, SignedInUser = user, HubIds = hubs, ProjectIds = projects,
                        PreviousUser = NoteAccountChange(user)
                    };
            }
        }
        catch (Exception ex)
        {
            snapshot = AccessSnapshot.Unreachable(user, OneLine(ex.Message));
        }

        lock (Gate) _cached = snapshot;
        return snapshot;
    }

    /// <summary>
    /// The verdict for one set against <paramref name="snapshot"/>. A null or unreachable snapshot
    /// is <see cref="AccessVerdict.Unknown"/>, as is a set that names no Forma project at all.
    /// </summary>
    public static AccessVerdict For(ModelPreset preset, AccessSnapshot? snapshot)
    {
        if (snapshot is not { Reachable: true }) return AccessVerdict.Unknown;

        int reachable = 0, denied = 0, unknown = 0;

        if (preset.IsFolderBacked)
        {
            // A folder-backed set IS its folders — its saved Models are only a snapshot fallback —
            // so the folders are what decides, and they have carried hub and project ids all along.
            foreach (FolderRef folder in preset.AllSourceFolders)
                Tally(folder.ProjectId, folder.HubId, snapshot, ref reachable, ref denied, ref unknown);
        }
        else
        {
            foreach (CloudModel model in preset.Models)
            {
                // A local .rvt needs no Forma account; it is neither evidence for nor against.
                if (model.IsLocal) continue;
                Tally(model.DmProjectId, model.HubId, snapshot, ref reachable, ref denied, ref unknown);
            }

            // Every model is local: nothing here depends on an account at all.
            if (reachable == 0 && denied == 0 && unknown == 0) return AccessVerdict.Accessible;
        }

        if (denied == 0) return reachable > 0 ? AccessVerdict.Accessible : AccessVerdict.Unknown;
        // Entries we could not identify are not counted as reachable — saying "partly fine" on the
        // strength of a blank id would be the same guess this class exists to avoid.
        return reachable > 0 ? AccessVerdict.Partial : AccessVerdict.NoAccess;
    }

    /// <summary>The same verdict against the snapshot already taken this session.</summary>
    public static AccessVerdict For(ModelPreset preset) => For(preset, Cached);

    /// <summary>
    /// Classifies one project reference. The hub is a fallback, not a second opinion: an entry with
    /// no project id but a hub this account cannot enumerate certainly cannot be opened, while a
    /// reachable hub says nothing about the individual project inside it.
    /// </summary>
    private static void Tally(string projectId, string hubId, AccessSnapshot snapshot,
        ref int reachable, ref int denied, ref int unknown)
    {
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            if (snapshot.ProjectIds.Contains(projectId)) reachable++;
            else denied++;
            return;
        }

        if (!string.IsNullOrWhiteSpace(hubId) && !snapshot.HubIds.Contains(hubId)) denied++;
        else unknown++;
    }

    /// <summary>
    /// Compares <paramref name="user"/> against the account the last session recorded, records the
    /// new one, and returns the OLD one when they differ (else "").
    ///
    /// <para>Only ever called for a REACHABLE snapshot. An unreachable one usually means Revit had
    /// not finished signing in, and treating that empty identity as "the account changed" would
    /// announce a change on nearly every cold start.</para>
    ///
    /// <para>The stored value is updated here rather than by the caller so the comparison cannot be
    /// made twice and reported twice; the answer lives on the snapshot instead.</para>
    /// </summary>
    private static string NoteAccountChange(string user)
    {
        if (user.Length == 0) return "";
        try
        {
            string? previous = Settings.UserSettings.LastSignedInUserId;
            Settings.UserSettings.LastSignedInUserId = user;
            return string.IsNullOrEmpty(previous) || string.Equals(previous, user, StringComparison.OrdinalIgnoreCase)
                ? ""
                : previous;
        }
        catch { return ""; /* best effort, exactly like the rest of UserSettings */ }
    }

    private static string OneLine(string text) =>
        text.Replace("\r", " ").Replace("\n", " ").Trim();
}
