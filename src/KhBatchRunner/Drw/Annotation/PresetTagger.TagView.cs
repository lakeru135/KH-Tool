using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Drw.Annotation;

internal static partial class PresetTagger
{
    private static void TagView(Document doc, View view, List<Resolved> rules, bool dryRun, bool repair,
        List<IReadOnlyDictionary<string, string>> rows, List<ElementId>? materialTags)
    {
        string kind = KindOf(view);
        double paperMm = DrwUnits.FromMillimeters(1) * Math.Max(1, view.Scale);
        double tolerance = 0.2 * paperMm;
        XYZ right = view.RightDirection, up = view.UpDirection;
        bool plan = view is ViewPlan;
        bool coarse = view.DetailLevel == ViewDetailLevel.Coarse;

        var pending = new List<Pending>();
        var viewRows = new List<(Pending? Item, IReadOnlyDictionary<string, string> Row)>();

        IReadOnlyDictionary<string, string> Row(Element e, Resolved r, string status, string note, string face = "", FamilySymbol? type = null) =>
            DrwRows.Row(Columns, kind, view.Name, face.Length > 0 ? $"{Label(e)} — {face}" : Label(e), r.CategoryName,
                type != null ? DrwLookup.TypeLabel(type) : r.Material ? MaterialLabel(r.Rule) : r.Rule.TagType, status, note);
        IReadOnlyDictionary<string, string> RowOf(Pending p, string status, string note) =>
            Row(p.Element, p.Rule, status, note, p.Face, p.Symbol);

        using Transaction t = DrwWarningSwallower.Start(doc, "Preset Tagger");

        // 0. Wall material tags that read "?", put back into their finish layer.
        bool moved = false;
        if (repair && view.ViewType != ViewType.Elevation)
            foreach ((string element, string type, string status, string note) in Repair(doc, view, coarse, paperMm, out moved))
                viewRows.Add((null, DrwRows.Row(Columns, kind, view.Name, element, "Material Tags", type, status, note)));

        Dictionary<ElementId, List<TagInfo>> tagged = ExistingTags(doc, view);

        void Remember(Element e, Element tag, bool material, XYZ? head)
        {
            if (!tagged.TryGetValue(e.Id, out List<TagInfo>? infos)) tagged[e.Id] = infos = new List<TagInfo>();
            infos.Add(new TagInfo { Family = (doc.GetElement(tag.GetTypeId()) as ElementType)?.FamilyName ?? "", Material = material, Head = head });
        }

        // Walls and columns read for which faces are seen, once per view.
        var solids = new Dictionary<ElementId, List<Solid>>();

        // 1. Every tag at its preferred spot.
        foreach (Resolved r in rules)
        {
            if (IsSpatial(r.Category) && !plan) continue;   // rooms and spaces are tagged in plans
            if (r.Material)
            {
                // A section uses both: the walls it cuts are tagged on the cut, the walls seen beyond
                // it as an elevation sees them.
                if (view.ViewType == ViewType.Elevation) PlanElevation(r);
                else
                {
                    PlanMaterial(r);
                    if (view.ViewType == ViewType.Section && r.Elevation != null) PlanElevation(r);
                }
                continue;
            }
            string family = r.Tag!.FamilyName;
            foreach (Element e in Candidates(doc, view, r).ToList())
            {
                if (tagged.TryGetValue(e.Id, out List<TagInfo>? infos) && infos.Any(i => !i.Material && (!r.Shared || i.Family == family)))
                {
                    viewRows.Add((null, Row(e, r, "Skipped", "Already tagged in this view.")));
                    continue;
                }
                XYZ? anchor = Anchor(e, view);
                if (anchor == null)
                {
                    viewRows.Add((null, Row(e, r, "Skipped", "No point to tag from (unplaced or not enclosed).")));
                    continue;
                }
                var p = new Pending { Rule = r, Element = e, Anchor = anchor, Symbol = r.Tag, Position = r.Rule.Position };
                if (r.Rule.PositionMode == TagPositionMode.Element)
                {
                    if (IsSpatial(r.Category))
                    {
                        p.Position = TagPosition.Center;
                        p.Note = "Rooms and spaces have no exterior side; placed on the room, by the view.";
                    }
                    else PlanSide(view, p, r.Rule.Side);
                }
                p.First = p.Out != null
                    ? anchor + p.Out * (p.Edge + r.Rule.DistanceMm * paperMm)
                    : anchor + Offset(p.Position, r.Rule.DistanceMm * paperMm, right, up);
                try
                {
                    Element? tag = Create(doc, view, e, r, p.First);
                    if (tag == null)
                    {
                        viewRows.Add((null, Row(e, r, "Failed", "Revit did not create the tag.")));
                        continue;
                    }
                    p.Tag = tag;
                    pending.Add(p);
                    viewRows.Add((p, Row(e, r, "Created", "")));
                    Remember(e, tag, false, null);
                }
                catch (Exception ex)
                {
                    viewRows.Add((null, Row(e, r, "Failed", ex.Message)));
                }
            }
        }

        // A wall material rule: one tag per finish face, its point 5 mm in from the finish face
        // (never on a layer boundary, never with a leader — both read "?"), and the type whose
        // triangle sits on the side of the face the view shows.
        void PlanMaterial(Resolved r)
        {
            bool perPiece = r.Rule.MaterialPerPiece;
            // A section with an elevation type: the walls it does not cut are the elevation's.
            bool beyondIsElevation = view.ViewType == ViewType.Section && r.Elevation != null;
            double minPiece = DrwUnits.FromMillimeters(Math.Max(0, r.Rule.MinPieceMm));
            // Every seen face of every wall first, so tags can line up across walls.
            var work = new List<FaceWork>();
            foreach (Element e in Candidates(doc, view, r).ToList())
            {
                if (e is not Wall w) continue;
                if (beyondIsElevation && !CutBySection(view, w)) continue;
                foreach (TagSide side in FacesOf(r.Rule.Faces))
                {
                    string faceName = side == TagSide.Exterior ? "exterior face" : "interior face";
                    if (coarse)
                    {
                        viewRows.Add((null, Row(e, r, "Skipped", CoarseNote, faceName)));
                        continue;
                    }
                    if (FaceOf(doc, view, w, side, out WallFace face, solids) is string why)
                    {
                        viewRows.Add((null, Row(e, r, "Skipped", why, faceName)));
                        continue;
                    }
                    (double ux, double uy) = Flat(face.Out, right, up);
                    if (Math.Sqrt(ux * ux + uy * uy) < 0.3)
                    {
                        viewRows.Add((null, Row(e, r, "Skipped", "This face is seen face-on in this view, so it shows no layers to tag.", faceName)));
                        continue;
                    }
                    TagPosition sits = Math.Abs(ux) >= Math.Abs(uy)
                        ? (ux > 0 ? TagPosition.Right : TagPosition.Left)
                        : (uy > 0 ? TagPosition.Above : TagPosition.Below);
                    FamilySymbol? sym = r.Materials.TryGetValue(sits, out FamilySymbol? s) ? s : null;
                    if (sym == null)
                    {
                        viewRows.Add((null, Row(e, r, "Failed", $"No material tag type is set for '{SitsName(sits)}', the side this face needs here.", faceName)));
                        continue;
                    }
                    // Only faces that are seen: another wall or a column against the face covers it
                    // (wholly: a finish drawn as its own wall) or cuts it into pieces (a wall meeting it).
                    if (r.Rule.MaterialSeenOnly)
                    {
                        Covers(doc, view, w, face, solids);
                        if (face.NotSeen(DrwUnits.FromMillimeters(SeenGapMm)))
                        {
                            Element? by = doc.GetElement(face.Covers.OrderByDescending(c => c.B - c.A).First().By);
                            viewRows.Add((null, Row(e, r, "Skipped",
                                $"Not a seen face: {(by != null ? Label(by) : "another element")} is against it (a finish drawn as its own " +
                                "wall, or a wall built up in layers), so it is not tagged.", faceName, sym)));
                            continue;
                        }
                    }
                    var pieces = perPiece ? face.Pieces(minPiece) : face.Whole(minPiece);
                    if (pieces.Count == 0)
                    {
                        viewRows.Add((null, Row(e, r, "Skipped",
                            $"No piece of the face between openings and walls is at least {Math.Max(0, r.Rule.MinPieceMm):0} mm long.", faceName, sym)));
                        continue;
                    }
                    work.Add(new FaceWork { Wall = w, Side = side, Face = face, Sits = sits, Symbol = sym, Name = faceName, Pieces = pieces, Turn = Turn(sits, ux, uy) });
                }
            }

            Dictionary<(FaceWork, int), (string Group, List<XYZ> Spots)> paired = PairAcross(work, !perPiece);

            foreach (FaceWork fw in work)
            {
                Wall w = fw.Wall;
                WallFace face = fw.Face;
                FamilySymbol sym = fw.Symbol;
                for (int k = 0; k < fw.Pieces.Count; k++)
                {
                    (double a, double b) = fw.Pieces[k];
                    string pieceName = fw.Pieces.Count > 1 ? $"{fw.Name}, piece {k + 1} of {fw.Pieces.Count}" : fw.Name;
                    // Already tagged: a material tag on this face (and, by piece, within this piece).
                    if (tagged.TryGetValue(w.Id, out List<TagInfo>? infos) && infos.Any(i => i.Material && i.Head != null &&
                            SideOf(doc, view, w, i.Head) == fw.Side &&
                            (!perPiece || face.Along(i.Head) is var at && at >= a - 1e-6 && at <= b + 1e-6)))
                    {
                        viewRows.Add((null, Row(w, r, "Skipped",
                            perPiece ? "This piece of the face already has a material tag in this view."
                                     : "This face already has a material tag in this view.", pieceName, sym)));
                        continue;
                    }
                    List<XYZ> spots;
                    string group;
                    if (paired.TryGetValue((fw, k), out var pair))
                    {
                        spots = pair.Spots;
                        group = pair.Group;
                    }
                    else
                    {
                        spots = face.Distances(a, b, !perPiece).Select(face.At).ToList();
                        group = $"{w.Id.Value}:{fw.Side}:{k}";
                    }
                    if (spots.Count == 0)
                    {
                        viewRows.Add((null, Row(w, r, "Skipped", "Every spot along the wall is in an opening or against another wall or column.", pieceName, sym)));
                        continue;
                    }
                    var p = new Pending
                    {
                        Rule = r, Element = w, Anchor = spots[0], First = spots[0], Alternates = spots.Skip(1).ToList(),
                        Fixed = true, Out = face.Out, LayerMaterial = face.MaterialAt((a + b) / 2), Position = fw.Sits, Face = pieceName, Symbol = sym,
                        Group = group, Side = fw.Side
                    };
                    try
                    {
                        if (!sym.IsActive) sym.Activate();
                        // A wall off the view's axes: the tag turns with it, so its triangle still points straight at the face.
                        IndependentTag? tag = IndependentTag.Create(doc, sym.Id, view.Id, new Reference(w), false,
                            fw.Turn == 0 ? TagOrientation.Horizontal : TagOrientation.AnyModelDirection, p.Anchor);
                        if (tag == null)
                        {
                            viewRows.Add((null, Row(w, r, "Failed", "Revit did not create the tag.", pieceName, sym)));
                            continue;
                        }
                        if (fw.Turn != 0) tag.RotationAngle = fw.Turn;
                        p.Tag = tag;
                        pending.Add(p);
                        viewRows.Add((p, RowOf(p, "Created", "")));
                        Remember(w, tag, true, p.Anchor);
                    }
                    catch (Exception ex)
                    {
                        viewRows.Add((null, Row(w, r, "Failed", ex.Message, pieceName, sym)));
                    }
                }
            }
        }

        // A wall material rule in an elevation: one tag on each surface of every face the elevation
        // looks at (each surface of a split or painted face its own), upright, of the elevation type
        // (the triangle points up at it), on a spot clear of openings and of anything in front.
        void PlanElevation(Resolved r)
        {
            FamilySymbol? sym = r.Elevation;
            XYZ toViewer = view.ViewDirection;
            double mm = DrwUnits.FromMillimeters(1);
            List<(Element E, BoundingBoxXYZ Box)>? blockers = null;
            foreach (Element e in Candidates(doc, view, r).ToList())
            {
                if (e is not Wall w) continue;
                if (view.ViewType == ViewType.Section && CutBySection(view, w)) continue;   // tagged on the cut
                foreach (TagSide side in FacesOf(r.Rule.Faces))
                {
                    string faceName = side == TagSide.Exterior ? "exterior face" : "interior face";
                    if (SideFace(w, side) is not PlanarFace pf)
                    {
                        viewRows.Add((null, Row(e, r, "Skipped", "The wall's face could not be read (only straight walls are material-tagged for now).", faceName)));
                        continue;
                    }
                    double facing = pf.FaceNormal.DotProduct(toViewer);
                    if (facing <= 0.05) continue;   // faces away: the elevation sees the wall's other face
                    if (facing < 0.5)
                    {
                        viewRows.Add((null, Row(e, r, "Skipped", "This face is seen at a glancing angle in this elevation, so it is not tagged.", faceName)));
                        continue;
                    }
                    if (sym == null)
                    {
                        viewRows.Add((null, Row(e, r, "Failed", "No material tag type is set for elevations.", faceName)));
                        continue;
                    }
                    blockers ??= Blockers(doc, view);
                    List<(List<Face> Faces, ElementId Material)> surfaces = Surfaces(doc, w, pf);
                    for (int k = 0; k < surfaces.Count; k++)
                    {
                        (List<Face> faces, ElementId material) = surfaces[k];
                        string pieceName = surfaces.Count > 1 ? $"{faceName}, surface {k + 1} of {surfaces.Count}" : faceName;
                        if (tagged.TryGetValue(w.Id, out List<TagInfo>? infos) &&
                            infos.Any(i => i.Material && i.Head != null && faces.Any(f => OnSurface(f, i.Head, 100 * mm))))
                        {
                            viewRows.Add((null, Row(w, r, "Skipped", "This surface already has a material tag in this view.", pieceName, sym)));
                            continue;
                        }
                        List<XYZ> spots = SurfaceSpots(faces).Where(p => InCrop(view, p)).ToList();
                        if (spots.Count == 0)
                        {
                            viewRows.Add((null, Row(w, r, "Skipped", "No spot on this surface is clear of its edges and openings inside the crop.", pieceName, sym)));
                            continue;
                        }
                        Element? hiddenBy = null;
                        var clear = new List<XYZ>();
                        foreach (XYZ p in spots)
                        {
                            if (Hidden(p, toViewer, view, blockers, solids, out Element? by)) hiddenBy ??= by;
                            else clear.Add(p);
                            if (clear.Count >= 16) break;
                        }
                        if (clear.Count == 0)
                        {
                            viewRows.Add((null, Row(w, r, "Skipped",
                                $"Hidden in this elevation behind {(hiddenBy != null ? Label(hiddenBy) : "another element")}, so it is not tagged.", pieceName, sym)));
                            continue;
                        }
                        var p0 = new Pending
                        {
                            Rule = r, Element = w, Anchor = clear[0], First = clear[0], Alternates = clear.Skip(1).ToList(),
                            Fixed = true, Out = pf.FaceNormal, LayerMaterial = material, Position = TagPosition.Below, Face = pieceName, Symbol = sym
                        };
                        try
                        {
                            if (!sym.IsActive) sym.Activate();
                            IndependentTag? tag = IndependentTag.Create(doc, sym.Id, view.Id, new Reference(w), false, TagOrientation.Horizontal, p0.Anchor);
                            if (tag == null)
                            {
                                viewRows.Add((null, Row(w, r, "Failed", "Revit did not create the tag.", pieceName, sym)));
                                continue;
                            }
                            p0.Tag = tag;
                            pending.Add(p0);
                            viewRows.Add((p0, RowOf(p0, "Created", "")));
                            Remember(w, tag, true, p0.Anchor);
                        }
                        catch (Exception ex)
                        {
                            viewRows.Add((null, Row(w, r, "Failed", ex.Message, pieceName, sym)));
                        }
                    }
                }
            }
        }

        if (pending.Count == 0)
        {
            Finish(viewRows.Select(v => (v.Item, v.Row)).ToList());
            return;
        }

        // 2. Measure, then settle each tag on the first spot clear of everything already there.
        doc.Regenerate();
        var mine = new HashSet<ElementId>(pending.Select(p => p.Tag.Id));
        List<Box> obstacles = Obstacles(doc, view, mine);
        var existing = new List<Box>(obstacles);
        var notes = new Dictionary<Pending, string>();
        var dropped = new HashSet<Pending>();
        // Which material tag each accepted box belongs to (by its index in obstacles): tags back to
        // back across a wall never count each other.
        var owner = new Dictionary<int, Pending>();
        bool Blocked(Box at, Pending? p) =>
            obstacles.Where((o, i) => p == null || !owner.TryGetValue(i, out Pending? q) || !OppositeFaces(p, q))
                .Any(o => o.Overlaps(at, tolerance));
        void Accept(Box at, Pending? p)
        {
            if (p?.Side != null) owner[obstacles.Count] = p;
            obstacles.Add(at);
        }
        Box? Rel(Pending p)
        {
            if (BoxOf(p.Tag, view) is not Box b) return null;
            (double fx, double fy) = Flat(p.First, right, up);
            return b.Shift(-fx, -fy);
        }

        // Both faces of a piece: the first spot along the wall where both are clear, so they line up.
        var settled = new HashSet<Pending>();
        foreach (var pair in pending.Where(p => p.Fixed && p.Group.Length > 0).GroupBy(p => p.Group).Where(g => g.Count() == 2))
        {
            Pending p1 = pair.First(), p2 = pair.Last();
            if (Rel(p1) is not Box r1 || Rel(p2) is not Box r2) continue;
            var a1 = new List<XYZ> { p1.Anchor }; a1.AddRange(p1.Alternates);
            var a2 = new List<XYZ> { p2.Anchor }; a2.AddRange(p2.Alternates);
            for (int i = 0; i < Math.Min(a1.Count, a2.Count); i++)
            {
                (double x1, double y1) = Flat(a1[i], right, up);
                (double x2, double y2) = Flat(a2[i], right, up);
                Box b1 = r1.Shift(x1, y1), b2 = r2.Shift(x2, y2);
                if (Blocked(b1, p1) || Blocked(b2, p2)) continue;
                Accept(b1, p1);
                Accept(b2, p2);
                if (!a1[i].IsAlmostEqualTo(p1.First)) Move(doc, p1, a1[i]);
                if (!a2[i].IsAlmostEqualTo(p2.First)) Move(doc, p2, a2[i]);
                if (i > 0)
                    notes[p1] = notes[p2] = "Moved along the wall, with the other face's tag: the first spot overlaps other annotation.";
                settled.Add(p1);
                settled.Add(p2);
                break;
            }
        }

        foreach (Pending p in pending)
        {
            if (settled.Contains(p)) continue;
            Box? measured = BoxOf(p.Tag, view);
            if (measured is not Box box)
            {
                notes[p] = "Its size could not be read, so overlaps were not checked.";
                continue;
            }
            (double fx, double fy) = Flat(p.First, right, up);
            // The tag's box relative to its head point, so it can be tried at any spot.
            Box rel = box.Shift(-fx, -fy);
            double d = p.Rule.Rule.DistanceMm * paperMm;
            XYZ? head = null;
            if (p.Out == null)
            {
                (double ax, double ay) = Flat(p.Anchor, right, up);
                foreach (TagPosition pos in Order(p.Position))
                {
                    (double ux, double uy) = Unit(pos);
                    double start = pos == p.Position ? d : Math.Max(d, -rel.Low(ux, uy) + GapMm * paperMm);
                    if (Clear(ux, uy, start, rel, ax, ay, obstacles, paperMm, tolerance) is not (double dx, double dy, double along)) continue;
                    head = p.Anchor + right * dx + up * dy;
                    if (pos != p.Position)
                        notes[p] = $"Moved {Word(pos)}: the preferred spot overlaps other annotation.";
                    else if (along > start + tolerance)
                        notes[p] = $"Moved {(along - start) / paperMm:0.#} mm further out: the preferred spot overlaps other annotation.";
                    Accept(rel.Shift(ax + dx, ay + dy), null);
                    break;
                }
            }
            else
            {
                // The element's own side only — an exterior tag never jumps to the interior.
                (double nx, double ny) = Flat(p.Out, right, up);
                double len = Math.Sqrt(nx * nx + ny * ny);
                double ux = nx / len, uy = ny / len;
                var anchors = new List<XYZ> { p.Anchor };
                anchors.AddRange(p.Alternates);
                for (int i = 0; i < anchors.Count && head == null; i++)
                {
                    (double ax, double ay) = Flat(anchors[i], right, up);
                    if (p.Fixed)
                    {
                        Box at = rel.Shift(ax, ay);
                        if (Blocked(at, p)) continue;
                        head = anchors[i];
                        Accept(at, p);
                    }
                    else
                    {
                        double start = p.Edge * len - rel.Low(ux, uy) + d;
                        if (Clear(ux, uy, start, rel, ax, ay, obstacles, paperMm, tolerance) is not (double dx, double dy, double along)) continue;
                        head = anchors[i] + right * dx + up * dy;
                        if (along > start + tolerance)
                            notes[p] = $"Moved {(along - start) / paperMm:0.#} mm further out: the first spot overlaps other annotation.";
                        Accept(rel.Shift(ax + dx, ay + dy), null);
                    }
                    if (i > 0) notes[p] = "Moved along the wall: the first spot overlaps other annotation.";
                }
            }
            if (head == null)
            {
                doc.Delete(p.Tag.Id);
                dropped.Add(p);
                continue;
            }
            if (!head.IsAlmostEqualTo(p.First)) Move(doc, p, head);
        }

        // 3. Read every kept tag back — that it exists, and where Revit actually drew it. A tag with
        // a leader is not checked: its box takes in the leader line. A material tag must also read
        // its material, not "?".
        doc.Regenerate();
        var drawn = new Dictionary<Pending, Box>();
        foreach (Pending p in pending)
            if (!dropped.Contains(p) && !p.Rule.Rule.Leader && p.Tag.IsValidObject && BoxOf(p.Tag, view) is Box b) drawn[p] = b;
        var final = new List<(Pending? Item, IReadOnlyDictionary<string, string> Row)>();
        foreach ((Pending? p, IReadOnlyDictionary<string, string> row) in viewRows)
        {
            if (p == null) { final.Add((null, row)); continue; }
            if (dropped.Contains(p))
            {
                final.Add((p, RowOf(p, "Skipped", p.Out != null
                    ? $"Every spot on its {(p.Fixed ? "face" : SideWord(p))} overlaps other annotation; no tag placed."
                    : "Every position overlaps other annotation; no tag placed.")));
                continue;
            }
            if (!p.Tag.IsValidObject || doc.GetElement(p.Tag.Id) == null)
            {
                final.Add((p, RowOf(p, "Failed", "Revit removed the tag.")));
                continue;
            }
            if (p.Fixed && MaterialHint(doc, p.LayerMaterial) is string bad)
            {
                final.Add((p, RowOf(p, "Warning", bad)));
                continue;
            }
            if (drawn.TryGetValue(p, out Box drawnBox) &&
                (existing.Any(o => o.Overlaps(drawnBox, tolerance)) ||
                 drawn.Any(d => d.Key != p && !OppositeFaces(p, d.Key) && d.Value.Overlaps(drawnBox, tolerance))))
            {
                final.Add((p, RowOf(p, "Warning", "Tagged, but as Revit drew it the tag overlaps other annotation.")));
                continue;
            }
            string note = string.Join(" ", new[] { p.Note, notes.TryGetValue(p, out string? n) ? n : "", p.Fixed ? UntouchedNote : "" }
                .Where(s => s.Length > 0));
            final.Add((p, RowOf(p, "Created", note)));
        }
        Finish(final);

        void Finish(List<(Pending? Item, IReadOnlyDictionary<string, string> Row)> list)
        {
            bool changed = pending.Count > 0 || moved;
            if (dryRun || !changed)
            {
                t.RollBack();
                rows.AddRange(list.Select(f =>
                    f.Item != null && f.Row["Status"] == "Created" ? With(f.Row, "Would create")
                    : f.Row["Status"] == "Repaired" ? With(f.Row, "Would repair")
                    : f.Row));
                return;
            }
            TransactionStatus status = t.Commit();
            foreach ((Pending? p, IReadOnlyDictionary<string, string> row) in list)
            {
                // A commit is not proof of the edit: the tag must still be there afterwards.
                if (p != null && row["Status"] is "Created" or "Warning" && (status != TransactionStatus.Committed || doc.GetElement(p.Tag.Id) == null))
                    rows.Add(With(row, "Failed", status != TransactionStatus.Committed ? $"The change was not kept ({status})." : "Revit removed the tag."));
                else if (p == null && row["Status"] == "Repaired" && status != TransactionStatus.Committed)
                    rows.Add(With(row, "Failed", $"The change was not kept ({status})."));
                else
                {
                    rows.Add(row);
                    if (p is { Fixed: true } && row["Status"] is "Created" or "Warning") materialTags?.Add(p.Tag.Id);
                }
            }
        }
    }
}
