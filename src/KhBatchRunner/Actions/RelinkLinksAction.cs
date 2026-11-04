using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KhBatchRunner.Actions;

/// <summary>
/// The cloud identity of one candidate relink TARGET (a model discovered by scanning the target
/// Forma folders), keyed in the action's map by its normalized name. Several candidates can share a
/// name (e.g. the currently-linked copy and the new copy both in scan range) — the action resolves
/// per link, excluding the copy the link already points at.
/// </summary>
public sealed class RelinkTarget
{
    public required string DisplayName { get; init; }
    public required string Region { get; init; }
    public required Guid ProjectGuid { get; init; }
    public required Guid ModelGuid { get; init; }

    /// <summary>Where the scan found this candidate — names the locations in an ambiguity report.</summary>
    public string FolderPath { get; init; } = "";
}

/// <summary>
/// Forma Batch Action — "Internal Relink": re-points each host model's Revit links to the matching
/// models found in the scanned target folders. Per host, per top-level link, in order:
/// <list type="number">
/// <item>Remap the link's name by its leading token: OLD code → NEW code (e.g. "OLD-ST-BLDA" →
/// "SITE1A-ST-BLDA"), the same site-code convention as Cut model. OLD == NEW is legal and means
/// "re-point same-named links" (the remap is then the identity).</item>
/// <item>If the leading token is not the OLD code and exact-name matching is on, fall back to the
/// link's own name — the incoming-Bridge / shared-model case (those names carry no site code).</item>
/// <item>Among the same-named candidates the command scanned, EXCLUDE the model the link already
/// references (read via <see cref="LinkCloudIdentity"/>): exactly one left → relink; none left →
/// "already points at the target"; several → reported ambiguous with their folder paths, never
/// guessed.</item>
/// <item><see cref="RevitLinkType.LoadFrom(ModelPath, WorksetConfiguration)"/> the link TYPE onto
/// the chosen target's cloud path (null config = keep the link's existing worksets), and report
/// the <see cref="LinkLoadResultType"/>.</item>
/// </list>
/// Category = Action: the engine opens the host attached and syncs the relinked references back to
/// the cloud central in <see cref="Batch.BatchEngine"/>'s Persist. Nested links are skipped (their
/// path is owned by the parent link). The link TYPE is collectible under the engine's CloseAllWorksets
/// open, and LoadFrom operates on the type, so the link instance's workset need not be open.
/// </summary>
public sealed class RelinkLinksAction : BatchActionBase
{
    public const string DisplayName = ActionNames.InternalRelink;

    private readonly string _oldCode;
    private readonly string _newCode;
    private readonly string _separator;
    private readonly bool _matchByExactName;
    // Normalized-name → ALL same-named candidates, resolved once by the command (one folder scan
    // for the run). Distinct GUIDs only; recurrences of the same model are de-duped by the command.
    private readonly IReadOnlyDictionary<string, List<RelinkTarget>> _targets;
    private string _note = "";

    public RelinkLinksAction(string oldCode, string newCode, string separator, bool matchByExactName,
        IReadOnlyDictionary<string, List<RelinkTarget>> targets)
    {
        _oldCode = (oldCode ?? "").Trim();
        _newCode = (newCode ?? "").Trim();
        _separator = string.IsNullOrEmpty(separator) ? "-" : separator;
        _matchByExactName = matchByExactName;
        _targets = targets;
    }

    public override string Name => DisplayName;

    // Write action: engine opens attached and syncs the relinked references back to central.
    public override ActionCategory Category => ActionCategory.Action;

    public override IReadOnlyList<string> Columns { get; } =
        new[] { "Link Name", "Expected Target", "Matched Model", "Result" };

    public override string PerModelNote => _note;

    // LoadFrom EDITS the RevitLinkType, and Revit throws "The element ... is in a closed workset"
    // under the engine's fast closed-worksets open (live-confirmed). So the engine reopens each
    // host with the candidate links' worksets open — only those; unrelated (heavy) worksets stay
    // closed, which is the point of the candidate filter below.
    public override WorksetPosture Worksets => WorksetPosture.PlansOwn;

    public override string WorksetPostureReason =>
        "Reads RevitLinkType on the CLOSED open — element types stay loaded whatever the worksets " +
        "do, which is the exception the whole posture rests on — then reopens naming only the " +
        "worksets holding the links it will actually re-point. Live since 0.9.1.";

    /// <summary>The worksets holding the link types (and their instances) this run will actually
    /// re-point — links whose <see cref="Match"/> resolves to a single viable target. Links that
    /// would be skipped/unmatched/left alone anyway don't force their worksets open.</summary>
    public override ICollection<WorksetId>? SelectWorksetsToOpen(Document doc)
    {
        var ids = new HashSet<WorksetId>();
        var candidateTypes = new HashSet<ElementId>();

        foreach (RevitLinkType type in new FilteredElementCollector(doc)
                     .OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
        {
            if (type.IsNestedLink) continue;
            if (Match(type).Kind != MatchKind.Relink) continue;
            candidateTypes.Add(type.Id);
            ids.Add(type.WorksetId);
        }
        if (candidateTypes.Count == 0) return null;

        // Instances of the candidate types too — the reload touches them as well.
        foreach (RevitLinkInstance inst in new FilteredElementCollector(doc)
                     .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            if (candidateTypes.Contains(inst.GetTypeId()))
                ids.Add(inst.WorksetId);
        }
        return ids;
    }

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        List<IReadOnlyDictionary<string, string>> rows = DoRelink(doc);
        return new Dictionary<string, string>
        {
            ["Link Name"] = $"{rows.Count} link(s)",
            ["Expected Target"] = "",
            ["Matched Model"] = "",
            ["Result"] = _note,
        };
    }

    public override IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(
        UIApplication uiApp, Document doc) => DoRelink(doc);

    /// <summary>One relink pass over the host document; fills <see cref="_note"/> with the roll-up.
    /// The engine calls exactly one of <see cref="Run"/> / <see cref="RunDetailRows"/> per model, so
    /// this runs once (no double LoadFrom).</summary>
    private List<IReadOnlyDictionary<string, string>> DoRelink(Document doc)
    {
        if (string.IsNullOrEmpty(_oldCode) || string.IsNullOrEmpty(_newCode))
            throw new InvalidOperationException("Internal Relink needs both an old and a new site code.");

        // Collect top-level link TYPES first (id + name); LoadFrom regenerates the document, so
        // capture the candidates before mutating. Iterate types (not instances) — one LoadFrom per
        // type covers all its instances.
        var candidates = new List<(ElementId Id, string Name)>();
        foreach (RevitLinkType type in new FilteredElementCollector(doc)
                     .OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
        {
            if (type.IsNestedLink) continue;
            candidates.Add((type.Id, type.Name));
        }

        var rows = new List<IReadOnlyDictionary<string, string>>();
        int relinked = 0, skipped = 0, notFound = 0, alreadyLinked = 0, ambiguous = 0, failed = 0;

        foreach ((ElementId id, string linkName) in candidates)
        {
            var type = (RevitLinkType)doc.GetElement(id);
            (MatchKind kind, string expected, RelinkTarget? target, string note) = Match(type);
            switch (kind)
            {
                case MatchKind.Skip:
                    skipped++;
                    rows.Add(Row(linkName, "", "", note));
                    continue;
                case MatchKind.NotFound:
                    notFound++;
                    rows.Add(Row(linkName, expected, "", note));
                    continue;
                case MatchKind.AlreadyLinked:
                    alreadyLinked++;
                    rows.Add(Row(linkName, expected, target?.DisplayName ?? "", note));
                    continue;
                case MatchKind.Ambiguous:
                    ambiguous++;
                    rows.Add(Row(linkName, expected, "", note));
                    continue;
            }

            // Workset state at the moment of the edit — when a relink fails "closed workset"
            // despite the engine's targeted reopen, this names the workset and its actual state.
            string wsState = DescribeWorksetState(doc, id);

            // A bridged model (in the folder but hosted in a project the account can't reach) can't
            // be relinked this way — LoadFrom from reconstructed cloud GUIDs can't resolve it; the
            // enriched error (exception type + GUIDs) makes that legible. Normal cloud models load
            // on the first attempt.
            LoadAttempt attempt = TryLoadFrom(type, ParseRegion(target!.Region), target.ProjectGuid, target.ModelGuid);
            if (attempt.Ok) relinked++; else failed++;
            rows.Add(Row(linkName, expected, target.DisplayName,
                attempt.Text + note + wsState +
                (attempt.Ok ? "" : $" [proj={target.ProjectGuid} model={target.ModelGuid}]")));
        }

        _note = candidates.Count == 0
            ? "no top-level Revit links"
            : $"{relinked} relinked, {skipped} skipped, {notFound} no target, " +
              $"{alreadyLinked} already linked, {ambiguous} ambiguous, {failed} failed";
        return rows;
    }

    private enum MatchKind { Skip, NotFound, Relink, AlreadyLinked, Ambiguous }

    /// <summary>Resolve one link to its relink outcome. Shared by <see cref="DoRelink"/> and
    /// <see cref="SelectWorksetsToOpen"/> so the workset plan and the actual pass can never
    /// disagree about which links get edited. Reading the link's current reference is legal on a
    /// closed workset; only the LoadFrom edit needs it open.</summary>
    private (MatchKind Kind, string Expected, RelinkTarget? Target, string Note) Match(RevitLinkType type)
    {
        string? expected = Remap(type.Name);
        bool byName = false;
        if (expected is null && _matchByExactName)
        {
            // Bridge/shared links carry no site code — match the link's own name as-is.
            expected = LinkWorksetNaming.StripRvt(type.Name);
            byName = true;
        }
        if (expected is null)
            return (MatchKind.Skip, "", null, "skipped — leading token is not the old code");

        if (!_targets.TryGetValue(Normalize(expected), out List<RelinkTarget>? candidates) || candidates.Count == 0)
            return (MatchKind.NotFound, expected, null, "no matching model in the scanned target folders");

        // The copy the link ALREADY references is never a relink candidate — with OLD == NEW (or a
        // bridged same-name copy) the scan legitimately sees both the current and the intended model.
        Guid? current = LinkCloudIdentity.TryGetCurrentModelGuid(type);
        List<RelinkTarget> viable = current is null
            ? candidates
            : candidates.Where(c => c.ModelGuid != current.Value).ToList();

        string suffix = byName ? " — matched by exact name" : "";
        if (viable.Count == 1)
            return (MatchKind.Relink, expected, viable[0], suffix);
        if (viable.Count == 0)
            return (MatchKind.AlreadyLinked, expected, candidates[0], "already points at the target — no change" + suffix);
        return (MatchKind.Ambiguous, expected, null,
            $"ambiguous — {viable.Count} same-named models, not guessing: " +
            string.Join("; ", viable.Select(c => string.IsNullOrEmpty(c.FolderPath) ? c.ModelGuid.ToString() : c.FolderPath)));
    }

    private readonly record struct LoadAttempt(bool Ok, string Text);

    /// <summary>One LoadFrom attempt onto a cloud path built from (region, projectGuid, modelGuid):
    /// "relinked" on success, else a description of the failure (path-build error, non-loaded result,
    /// or the exception type+message — e.g. RevitServerUnauthorizedException for an inaccessible
    /// project). Never throws.</summary>
    private static LoadAttempt TryLoadFrom(RevitLinkType type, string region, Guid projectGuid, Guid modelGuid)
    {
        ModelPath path;
        try
        {
            path = ModelPathUtils.ConvertCloudGUIDsToCloudPath(region, projectGuid, modelGuid);
        }
        catch (Exception ex)
        {
            return new LoadAttempt(false, $"path-build {ex.GetType().Name}: {ex.Message}");
        }
        try
        {
            LinkLoadResult result = type.LoadFrom(path, null);
            bool ok = result.LoadResult == LinkLoadResultType.LinkLoaded;
            return new LoadAttempt(ok, ok ? "relinked" : "LoadFrom returned " + result.LoadResult);
        }
        catch (Exception ex)
        {
            return new LoadAttempt(false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>" [doc=X ws 'name' open/CLOSED]" for the link type element — names which workset the
    /// edit needs and whether it is actually open right now, plus whether the host document itself is
    /// a real primary document or a lingering LINK instance (doc.IsLinked — an open-as-link document
    /// can never have its worksets opened, whatever the open options said).</summary>
    private static string DescribeWorksetState(Document doc, ElementId typeId)
    {
        try
        {
            var type = doc.GetElement(typeId);
            Workset ws = doc.GetWorksetTable().GetWorkset(type.WorksetId);
            return $" [doc={(doc.IsLinked ? "LINKED" : "primary")}; ws '{ws.Name}' {(ws.IsOpen ? "open" : "CLOSED")}]";
        }
        catch (Exception ex)
        {
            return " [ws state unreadable: " + ex.Message + "]";
        }
    }

    private static IReadOnlyDictionary<string, string> Row(string link, string expected, string matched, string result) =>
        new Dictionary<string, string>
        {
            ["Link Name"] = link,
            ["Expected Target"] = expected,
            ["Matched Model"] = matched,
            ["Result"] = result,
        };

    /// <summary>The expected NEW target name for a host link, or null when the link's leading token
    /// (text before the first separator) is not the OLD code. Keeps the link's separator + remainder,
    /// swapping only the leading token: "OLD-ST-BLDA" (old "OLD") → "SITE1A-ST-BLDA". With OLD == NEW this
    /// is the identity — the same-prefix re-point case.</summary>
    private string? Remap(string linkName)
    {
        string name = LinkWorksetNaming.StripRvt(linkName);
        int i = name.IndexOf(_separator, StringComparison.Ordinal);
        string lead = i >= 0 ? name[..i] : name;
        if (!lead.Equals(_oldCode, StringComparison.OrdinalIgnoreCase)) return null;
        return _newCode + name[lead.Length..]; // keep the separator + rest
    }

    /// <summary>Key used for both the target map and the expected-name lookup: lower-cased, no ".rvt".</summary>
    public static string Normalize(string name) =>
        LinkWorksetNaming.StripRvt(name).Trim().ToLowerInvariant();

    private static string ParseRegion(string region) =>
        string.Equals(region, "EMEA", StringComparison.OrdinalIgnoreCase)
            ? ModelPathUtils.CloudRegionEMEA
            : ModelPathUtils.CloudRegionUS;
}
