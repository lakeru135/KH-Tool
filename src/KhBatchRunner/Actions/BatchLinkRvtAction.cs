using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Batch;
using KhBatchRunner.Presets;

namespace KhBatchRunner.Actions;

/// <summary>
/// Multi Model Action — "Batch Link RVT": links one or more SOURCE models into every model of a set,
/// and puts each new link on the workset the convention says it belongs on.
///
/// <para>This is the first action in the add-in that CREATES a link. Everything else that touches
/// links only reads them (<see cref="GetRvtLinksWorksetAction"/>), re-points one that already exists
/// (<see cref="RelinkLinksAction"/>, via <c>RevitLinkType.LoadFrom</c>) or moves one that is already
/// there (<see cref="CorrectWorksetAction"/>). So <c>RevitLinkType.Create</c> and
/// <c>RevitLinkInstance.Create</c> are new API surface here, deliberately confined to
/// <see cref="LinkOneSource"/>.</para>
///
/// <para><b>The workset is the point.</b> A link lands on whatever workset happens to be active,
/// which is why hand-linked models routinely violate the one-link-per-workset convention and why
/// Correct Workset exists to clean up after them. Mode B names each link's workset through the SHARED
/// <see cref="LinkWorksetNaming.Expected"/> that Correct Workset itself uses — so a Correct Workset
/// run straight after a link run reports "already correct" rather than moving everything that was
/// just placed. That agreement is asserted offline in <c>tools/LivenessSim</c>.</para>
///
/// <para>Category = Action: the engine opens each host attached and syncs the new links back to the
/// cloud central in <see cref="Batch.BatchEngine"/>'s Persist. Nested links are never created (their
/// path is owned by the parent link).</para>
/// </summary>
public sealed class BatchLinkRvtAction : BatchActionBase
{
    public const string DisplayName = "Batch Link RVT";

    private readonly BatchLinkRvtInputSet _inputs;
    private string _note = "";

    public BatchLinkRvtAction(BatchLinkRvtInputSet inputs) => _inputs = inputs;

    public override string Name => DisplayName;

    // Write action: engine opens attached and syncs the new links back to central.
    public override ActionCategory Category => ActionCategory.Action;

    public override IReadOnlyList<string> Columns { get; } =
        new[] { "Source", "Link Name", "Workset", "Result" };

    public override string PerModelNote => _note;

    // Creating a RevitLinkType/RevitLinkInstance is an EDIT, and an element can only be placed on a
    // workset that is open. The destinations are worked out from the cheap closed open and named for
    // the reopen; anything that does not exist yet is created inside this action's own transaction,
    // which needs no reopen because a freshly created workset is open.
    public override WorksetPosture Worksets => WorksetPosture.PlansOwn;

    public override string WorksetPostureReason =>
        "Reads RevitLinkType (a TYPE, which stays loaded whatever the worksets do — the exception " +
        "the whole posture rests on) to decide which sources are already linked, then reopens naming " +
        "only the destination worksets it will place links on. NOTE the deliberate limit that implies: " +
        "'already linked' is decided at TYPE level only. A RevitLinkInstance is an INSTANCE and is " +
        "invisible on the closed open, so this action cannot tell a link type with no placed instance " +
        "from one with several, and does not claim to — saying otherwise would need OpensAll and a " +
        "13–30 minute open per host. Accepted by the author, 2026-08-27.";

    /// <summary>
    /// The destination worksets that ALREADY exist, so the engine can reopen the host with them open.
    /// A destination that does not exist yet is not named here and does not need to be: it is created
    /// inside the action's transaction, and a workset Revit has just created is open.
    ///
    /// <para>In Mode B this can only GUESS the name, from the source's display name, because the real
    /// name comes from <c>RevitLinkType.Name</c> and that type does not exist yet. A wrong guess is
    /// not silent: <see cref="LinkOneSource"/> recomputes from the created type and reports the
    /// difference on the row.</para>
    ///
    /// <para>The existing links' own worksets are never named — that is what stops a host downloading
    /// its whole federated tree just to gain one link.</para>
    /// </summary>
    public override ICollection<WorksetId>? SelectWorksetsToOpen(Document doc)
    {
        if (!doc.IsWorkshared) return null;

        Dictionary<string, Workset> byName = ReadWorksets(doc);
        var ids = new HashSet<WorksetId>();
        foreach (CloudModel source in _inputs.Sources)
        {
            string guess = BatchLinkRules.WorksetNameFor(_inputs, source.DisplayName);
            if (guess.Length > 0 && byName.TryGetValue(guess, out Workset? ws)) ids.Add(ws.Id);
        }
        return ids.Count == 0 ? null : ids;
    }

    public override IReadOnlyDictionary<string, string> Run(UIApplication uiApp, Document doc)
    {
        List<IReadOnlyDictionary<string, string>> rows = DoLink(doc);
        return new Dictionary<string, string>
        {
            ["Source"] = $"{rows.Count} row(s)",
            ["Link Name"] = "",
            ["Workset"] = "",
            ["Result"] = _note,
        };
    }

    public override IReadOnlyList<IReadOnlyDictionary<string, string>> RunDetailRows(
        UIApplication uiApp, Document doc) => DoLink(doc);

    /// <summary>One link pass over the host; fills <see cref="_note"/> with the roll-up. The engine
    /// calls exactly one of Run/RunDetailRows per model, so this runs once (no double linking).</summary>
    private List<IReadOnlyDictionary<string, string>> DoLink(Document doc)
    {
        if (_inputs.Sources.Count == 0)
            throw new InvalidOperationException("Batch Link RVT has no source models to link.");

        var rows = new List<IReadOnlyDictionary<string, string>>();
        List<ExistingLink> existing = ReadExistingLinks(doc);
        int linked = 0, skipped = 0, failed = 0;

        foreach (CloudModel source in _inputs.Sources)
        {
            if (_inputs.SkipIfAlreadyLinked && BatchLinkRules.AlreadyLinked(existing, source))
            {
                skipped++;
                rows.Add(Row(source.DisplayName, "", "", "already linked — skipped"));
                continue;
            }

            // ONE TRANSACTION PER SOURCE, not one per host: a source that cannot be resolved must
            // cost its own row, not every link the host was also given. The same rule Correct Workset
            // states for its per-element edits.
            if (LinkOneSource(doc, source, rows)) linked++; else failed++;
        }

        _note = $"{linked} linked, {skipped} skipped, {failed} failed";
        return rows;
    }

    /// <summary>
    /// Create one link, place one instance, and put both on the right workset. Returns false (with a
    /// row explaining why) rather than throwing — one bad source must never fail the host.
    /// </summary>
    private bool LinkOneSource(Document doc, CloudModel source, List<IReadOnlyDictionary<string, string>> rows)
    {
        ModelPath path;
        try
        {
            path = PathFor(source);
        }
        catch (Exception ex)
        {
            rows.Add(Row(source.DisplayName, "", "", $"path-build {ex.GetType().Name}: {ex.Message}"));
            return false;
        }

        bool workshared = doc.IsWorkshared;
        string guess = BatchLinkRules.WorksetNameFor(_inputs, source.DisplayName);
        ElementId typeId = ElementId.InvalidElementId;
        string typeName = "", finalWorkset = "", extra = "";

        // Rows written INSIDE the transaction (a created workset) describe things that stop existing
        // if it rolls back. Remembering where this source's rows begin is what lets them be taken
        // back with it — otherwise a failed link leaves a log claiming a workset the model never got.
        int rowMark = rows.Count;

        using var t = new Transaction(doc, "Batch Link RVT — " + source.DisplayName);
        try
        {
            t.Start();

            // Pre-place on the guessed destination so the new elements are BORN correct where the
            // guess is right (which is always, in Mode A). Best effort: the move below is the contract.
            Dictionary<string, Workset> byName = workshared
                ? ReadWorksets(doc)
                : new Dictionary<string, Workset>(StringComparer.OrdinalIgnoreCase);
            if (workshared && guess.Length > 0)
                TrySetActive(doc, EnsureWorkset(doc, byName, guess, rows, source.DisplayName));

            // The link's OWN worksets stay closed. Create resolves and downloads the source right
            // here, inside the transaction; without this it pulls the source's entire workset tree
            // into a host that wanted one link. Free, and the largest cost in the whole action.
            var options = new RevitLinkOptions(
                false, new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets));
            LinkLoadResult result = RevitLinkType.Create(doc, path, options);
            if (result.LoadResult != LinkLoadResultType.LinkLoaded || result.ElementId is null)
            {
                t.RollBack();
                Undo(rows, rowMark);
                rows.Add(Row(source.DisplayName, "", guess, "Create returned " + result.LoadResult));
                return false;
            }

            typeId = result.ElementId;
            var type = (RevitLinkType)doc.GetElement(typeId);

            // Revit owns the type's name, and it is free not to be the source's display name (it
            // disambiguates a name already in use). Correct Workset reads the TYPE's name, so the
            // workset must be derived from the same string or the two ends of the convention drift.
            typeName = type.Name;
            finalWorkset = BatchLinkRules.WorksetNameFor(_inputs, typeName);
            if (!finalWorkset.Equals(guess, StringComparison.OrdinalIgnoreCase))
                extra += $"; Revit named the link '{typeName}', so its workset is '{finalWorkset}', not '{guess}'";

            type.AttachmentType = _inputs.Attachment ? AttachmentType.Attachment : AttachmentType.Overlay;

            RevitLinkInstance instance = CreateInstance(doc, typeId, ref extra);
            if (_inputs.PinInstance)
            {
                try { instance.Pinned = true; }
                catch (Exception ex) { extra += "; pin failed: " + ex.Message; }
            }

            if (workshared && finalWorkset.Length > 0)
            {
                Workset? dest = EnsureWorkset(doc, byName, finalWorkset, rows, source.DisplayName);
                extra += MoveToWorkset(dest, finalWorkset, type, instance);
            }
            else if (!workshared)
            {
                finalWorkset = "";
                extra += "; host is not workshared — no workset assigned";
            }

            t.Commit();
        }
        catch (Exception ex)
        {
            try { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); } catch { /* nothing left to save */ }
            Undo(rows, rowMark);
            rows.Add(Row(source.DisplayName, typeName, finalWorkset, $"{ex.GetType().Name}: {ex.Message}"));
            return false;
        }

        // The commit is NOT proof the link survived it. DialogSuppressor answers an otherwise
        // unresolvable failure by DELETING the failing elements and letting the commit proceed —
        // which would be reported as a success, synced, and leave no link behind. Ask the document.
        Element? survived = null;
        try { survived = doc.GetElement(typeId); } catch { /* treated as gone */ }
        if (survived is null)
        {
            rows.Add(Row(source.DisplayName, typeName, finalWorkset,
                "link was REMOVED during the commit (a Revit failure resolved by deleting it)" + extra));
            return false;
        }

        rows.Add(Row(source.DisplayName, typeName, finalWorkset, "linked" + extra));
        return true;
    }

    /// <summary>Place the instance, degrading a shared-coordinates failure to origin-to-origin rather
    /// than losing the link: Shared throws when the host has no site matching the link's.</summary>
    private RevitLinkInstance CreateInstance(Document doc, ElementId typeId, ref string extra)
    {
        ImportPlacement placement = _inputs.Placement switch
        {
            LinkPlacement.Origin => ImportPlacement.Origin,
            LinkPlacement.Centered => ImportPlacement.Centered,
            _ => ImportPlacement.Shared,
        };
        if (placement != ImportPlacement.Shared)
            return RevitLinkInstance.Create(doc, typeId, placement);

        try
        {
            return RevitLinkInstance.Create(doc, typeId, ImportPlacement.Shared);
        }
        catch (Exception ex)
        {
            extra += "; shared coordinates unavailable (" + ex.Message + ") — placed origin to origin";
            return RevitLinkInstance.Create(doc, typeId, ImportPlacement.Origin);
        }
    }

    /// <summary>Move the link type AND its instance onto <paramref name="dest"/>. The active-workset
    /// route above covers a freshly created workset; this covers one that already existed, and is what
    /// makes the outcome asserted rather than assumed. A closed destination cannot be written to —
    /// which is exactly what <see cref="SelectWorksetsToOpen"/> is for, so say so when it happens.</summary>
    private static string MoveToWorkset(Workset? dest, string name, Element type, Element instance)
    {
        if (dest is null) return $"; workset '{name}' could not be created — link left where it landed";

        var stuck = new List<string>();
        foreach (Element e in new[] { type, instance })
        {
            if (e.WorksetId == dest.Id) continue;
            Parameter? p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
            // Read-only partition param = this element cannot move (owned by another user, etc).
            if (p is null || p.IsReadOnly) { stuck.Add(Describe(e)); continue; }
            try { p.Set(dest.Id.IntegerValue); }
            catch (Exception ex) { stuck.Add(Describe(e) + " (" + ex.Message + ")"); }
        }
        if (stuck.Count == 0) return "";

        return $"; could not move {string.Join(" + ", stuck)} onto '{name}'" +
               (dest.IsOpen ? "" : " — that workset is CLOSED in this host");
    }

    private static string Describe(Element e) => e is RevitLinkType ? "the link type" : "the link instance";

    /// <summary>Drop the rows this source wrote inside a transaction that then rolled back — they
    /// describe worksets the model no longer has.</summary>
    private static void Undo(List<IReadOnlyDictionary<string, string>> rows, int mark)
    {
        if (rows.Count > mark) rows.RemoveRange(mark, rows.Count - mark);
    }

    /// <summary>The cloud path for a Forma source, or the user-visible path for a local one.</summary>
    private static ModelPath PathFor(CloudModel source) =>
        source.IsLocal
            ? ModelPathUtils.ConvertUserVisiblePathToModelPath(source.LocalPath!)
            : ModelPathUtils.ConvertCloudGUIDsToCloudPath(
                ParseRegion(source.Region), source.ProjectGuid, source.ModelGuid);

    private static string ParseRegion(string region) =>
        string.Equals(region, "EMEA", StringComparison.OrdinalIgnoreCase)
            ? ModelPathUtils.CloudRegionEMEA
            : ModelPathUtils.CloudRegionUS;

    /// <summary>
    /// What this host already links, as comparable identities. TYPES only — a <c>RevitLinkType</c>
    /// stays loaded whatever the worksets do, so this is complete on the cheap closed open in a way
    /// an instance scan could never be (see <see cref="WorksetPostureReason"/>). Nested links are
    /// included: a source already present under a parent link should not be linked again at top
    /// level. A link whose reference cannot be read contributes nothing, so it can never silently
    /// suppress a source the user asked for.
    /// </summary>
    private static List<ExistingLink> ReadExistingLinks(Document doc)
    {
        var found = new List<ExistingLink>();
        foreach (RevitLinkType type in new FilteredElementCollector(doc)
                     .OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
        {
            Guid? cloud = LinkCloudIdentity.TryGetCurrentModelGuid(type);
            string? local = cloud is null ? TryGetLocalPath(type) : null;
            if (cloud is null && local is null) continue;
            found.Add(new ExistingLink(cloud, local));
        }
        return found;
    }

    /// <summary>The absolute path a LOCAL-file link points at — <see cref="LinkCloudIdentity"/>
    /// returns the cloud model GUID and nothing else, so a local source needs its own comparison.</summary>
    private static string? TryGetLocalPath(RevitLinkType type)
    {
        try
        {
            if (!type.IsExternalFileReference()) return null;
            ModelPath mp = type.GetExternalFileReference().GetAbsolutePath();
            if (mp is null || mp.CloudPath) return null;
            string visible = ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
            return string.IsNullOrWhiteSpace(visible) ? null : visible;
        }
        catch { return null; }
    }

    /// <summary>Creates <paramref name="name"/> when absent; null when it could not be created.
    /// Mirrors <c>CorrectWorksetAction.EnsureWorkset</c> — same convention, same reporting.</summary>
    private static Workset? EnsureWorkset(Document doc, Dictionary<string, Workset> byName, string name,
        List<IReadOnlyDictionary<string, string>> rows, string sourceName)
    {
        if (byName.TryGetValue(name, out Workset? existing)) return existing;
        try
        {
            Workset ws = Workset.Create(doc, name);
            byName[name] = ws;
            rows.Add(Row(sourceName, "", name, "CREATED workset"));
            return ws;
        }
        catch (Exception ex)
        {
            rows.Add(Row(sourceName, "", name, "workset create FAILED: " + ex.Message));
            return null;
        }
    }

    /// <summary>Best effort — a closed or otherwise unusable workset simply leaves the new elements
    /// where Revit puts them, and the <c>ELEM_PARTITION_PARAM</c> move is what corrects that.</summary>
    private static void TrySetActive(Document doc, Workset? ws)
    {
        if (ws is null) return;
        try { doc.GetWorksetTable().SetActiveWorksetId(ws.Id); } catch { /* the move handles it */ }
    }

    /// <summary>Live name → Workset map for the document's USER worksets (the shape
    /// <c>CorrectWorksetAction.ReadWorksets</c> uses).</summary>
    private static Dictionary<string, Workset> ReadWorksets(Document doc)
    {
        var byName = new Dictionary<string, Workset>(StringComparer.OrdinalIgnoreCase);
        foreach (Workset ws in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
            byName[ws.Name] = ws;
        return byName;
    }

    private static IReadOnlyDictionary<string, string> Row(string source, string link, string workset, string result) =>
        new Dictionary<string, string>
        {
            ["Source"] = source,
            ["Link Name"] = link,
            ["Workset"] = workset,
            ["Result"] = result,
        };
}
