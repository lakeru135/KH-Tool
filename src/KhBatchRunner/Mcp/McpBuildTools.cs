#if !EDITION_DRW
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Drw.Common;
using View = Autodesk.Revit.DB.View;

namespace KhBatchRunner.Mcp;

/// <summary>
/// The AI Bridge's build tools: a new model and saving it, views, crops, text, 2D drawings from a
/// PDF's vectors and a PDF page as an underlay. They exist because rebuilding a drawing set from a
/// PDF needs them on nearly every sheet, and run_code can't do the save at all (it runs inside the
/// bridge's TransactionGroup, and Revit refuses to save with a group open).
/// <para>Lengths: plan positions in metres (model), paper positions in mm. A view's paper mm are
/// model mm divided by its scale; a sheet's are model mm.</para>
/// </summary>
internal static partial class McpTools
{
    // ------------------------------------------------------------------ new_model / save_model

    private static JsonNode NewModel(UIApplication app, string path, string? template, bool overwrite)
    {
        if (!path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) throw new McpError("new_model writes a .rvt path.");
        string full = Path.GetFullPath(path);
        if (File.Exists(full) && !overwrite) throw new McpError($"'{full}' exists. Pass overwrite:true to replace it, or pick another name.");
        string rte = template ?? app.Application.DefaultProjectTemplate;
        if (string.IsNullOrEmpty(rte) || !File.Exists(rte)) throw new McpError($"No project template at '{rte}'. Name one with 'template'.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        Document made = app.Application.NewProjectDocument(rte);
        made.SaveAs(full, new SaveAsOptions { OverwriteExistingFile = true });
        made.Close(false);
        JsonNode status = OpenModel(app, full);
        status["template"] = rte;
        return status;
    }

    private static JsonNode SaveModel(Document doc, string? path, bool overwrite)
    {
        if (doc.IsReadOnly) throw new McpError("The model is read-only.");
        // The AI never syncs into a shared model, so a workshared file is left to the user.
        if (doc.IsWorkshared) throw new McpError("This model is workshared: save and sync it in Revit yourself.");
        if (path == null)
        {
            if (string.IsNullOrEmpty(doc.PathName)) throw new McpError("The model was never saved: give a 'path'.");
            doc.Save();
        }
        else
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full) && !overwrite && !string.Equals(full, doc.PathName, StringComparison.OrdinalIgnoreCase))
                throw new McpError($"'{full}' exists. Pass overwrite:true to replace it.");
            doc.SaveAs(full, new SaveAsOptions { OverwriteExistingFile = true });
        }
        return new JsonObject { ["saved"] = doc.PathName, ["modified"] = doc.IsModified };
    }

    // ------------------------------------------------------------------ create_view

    private static XYZ M2(JsonNode? n, double z = 0)
    {
        if (n is not JsonArray a || a.Count < 2) throw new McpError("A point is [x, y] in metres.");
        return new XYZ(DrwUnits.FromMeters((double)a[0]!), DrwUnits.FromMeters((double)a[1]!),
            a.Count > 2 ? DrwUnits.FromMeters((double)a[2]!) : z);
    }

    private static Level FindLevel(Document doc, string key) =>
        new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .FirstOrDefault(l => l.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? throw new McpError($"No level '{key}'. Levels: " + string.Join(" | ",
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).Select(l => l.Name)) + ".");

    private static ElementId ViewTypeFor(Document doc, ViewFamily family, string? typeName)
    {
        List<ViewFamilyType> types = DrwLookup.ViewTypes(doc, family);
        if (types.Count == 0) throw new McpError($"The model has no {family} view type.");
        if (typeName == null) return types[0].Id;
        return (types.FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                ?? throw new McpError($"No {family} view type '{typeName}'. Types: " + string.Join(" | ", types.Select(t => t.Name)) + ".")).Id;
    }

    private static JsonObject CreateView(Document doc, JsonObject a)
    {
        string kind = Str(a, "kind").ToLowerInvariant();
        string name = Str(a, "name");
        if (DrwNaming.ExistingViewNames(doc).Contains(name)) throw new McpError($"A view named '{name}' exists already.");
        string? typeName = OptStr(a, "view_type");
        var notes = new List<string>();

        using Transaction t = new Transaction(doc, "Create view " + name);
        t.Start();
        View view;
        switch (kind)
        {
            case "plan":
            case "ceiling":
            case "structural_plan":
            {
                ViewFamily fam = kind == "plan" ? ViewFamily.FloorPlan : kind == "ceiling" ? ViewFamily.CeilingPlan : ViewFamily.StructuralPlan;
                Level level = FindLevel(doc, Str(a, "level"));
                view = ViewPlan.Create(doc, ViewTypeFor(doc, fam, typeName), level.Id);
                break;
            }
            case "section":
            {
                // A section line from 'from' to 'to' looking to its right (as Revit draws one), or to its
                // left with flip; 'depth_m' deep, from 'bottom_m' to 'top_m' (absolute heights).
                XYZ p0 = M2(a["from"]), p1 = M2(a["to"]);
                XYZ along = (p1 - p0);
                if (along.GetLength() < 1e-6) throw new McpError("A section needs two different points.");
                along = new XYZ(along.X, along.Y, 0).Normalize();
                XYZ look = (bool?)a["flip"] == true ? XYZ.BasisZ.CrossProduct(along) : along.CrossProduct(XYZ.BasisZ);
                // Tested on Revit 2026.5: the section looks along +BasisZ of the box transform, and the
                // box's 0..depth runs that way too. BasisX x BasisY (up) = BasisZ.
                XYZ basisZ = look, basisX = XYZ.BasisZ.CrossProduct(basisZ).Normalize();
                double bottom = DrwUnits.FromMeters((double?)a["bottom_m"] ?? -0.5);
                double top = DrwUnits.FromMeters((double?)a["top_m"] ?? 6);
                double depth = DrwUnits.FromMeters((double?)a["depth_m"] ?? 10);
                double half = (p1 - p0).GetLength() / 2;
                XYZ mid = (p0 + p1) / 2;
                var tf = Transform.Identity;
                tf.Origin = new XYZ(mid.X, mid.Y, 0);
                tf.BasisX = basisX;
                tf.BasisY = XYZ.BasisZ;
                tf.BasisZ = basisZ;
                var box = new BoundingBoxXYZ { Transform = tf, Min = new XYZ(-half, bottom, 0), Max = new XYZ(half, top, depth) };
                view = ViewSection.CreateSection(doc, ViewTypeFor(doc, ViewFamily.Section, typeName), box);
                break;
            }
            case "elevation":
            {
                // A marker at 'at', facing one of N/E/S/W (the side looked TOWARD), drawn in 'plan'.
                string facing = (OptStr(a, "facing") ?? "N").ToUpperInvariant();
                XYZ want = facing switch
                {
                    "N" => XYZ.BasisY, "S" => -XYZ.BasisY, "E" => XYZ.BasisX, "W" => -XYZ.BasisX,
                    _ => throw new McpError("facing is N, E, S or W (the direction the elevation looks).")
                };
                View plan = FindView(doc, Str(a, "plan"));
                if (plan is not ViewPlan) throw new McpError("'plan' must be a floor plan for the elevation marker.");
                ElevationMarker marker = ElevationMarker.CreateElevationMarker(doc, ViewTypeFor(doc, ViewFamily.Elevation, typeName),
                    M2(a["at"]), Math.Max(1, (int?)a["scale"] ?? 100));
                ViewSection? found = null;
                for (int i = 0; i < 4 && found == null; i++)
                {
                    ViewSection candidate = marker.CreateElevation(doc, plan.Id, i);
                    // ViewDirection points out of the screen, at the viewer: the view looks the other way.
                    if (candidate.ViewDirection.Negate().IsAlmostEqualTo(want, 1e-6)) found = candidate;
                    else doc.Delete(candidate.Id);
                }
                view = found ?? throw new McpError("Revit made no elevation facing " + facing + ".");
                break;
            }
            case "3d":
            {
                View3D v3 = View3D.CreateIsometric(doc, ViewTypeFor(doc, ViewFamily.ThreeDimensional, typeName));
                if (a["direction"] is JsonArray d && d.Count == 3)
                {
                    XYZ forward = new XYZ((double)d[0]!, (double)d[1]!, (double)d[2]!).Normalize();
                    XYZ up = forward.CrossProduct(XYZ.BasisZ).CrossProduct(forward).Normalize();
                    v3.SetOrientation(new ViewOrientation3D(-forward * 100, up, forward));
                }
                if (a["section_box"] is JsonObject sb)
                {
                    v3.IsSectionBoxActive = true;
                    v3.SetSectionBox(new BoundingBoxXYZ { Min = M2(sb["min"]), Max = M2(sb["max"]) });
                }
                view = v3;
                break;
            }
            case "drafting":
                view = ViewDrafting.Create(doc, ViewTypeFor(doc, ViewFamily.Drafting, typeName));
                break;
            default:
                throw new McpError("kind is plan, ceiling, structural_plan, section, elevation, 3d or drafting.");
        }
        view.Name = name;
        if ((int?)a["scale"] is int scale && scale > 0)
        {
            try { view.Scale = scale; } catch { notes.Add("its template controls the scale"); }
        }
        if (OptStr(a, "detail_level") is string dl)
            view.DetailLevel = dl.ToLowerInvariant() switch { "coarse" => ViewDetailLevel.Coarse, "medium" => ViewDetailLevel.Medium, _ => ViewDetailLevel.Fine };
        if (OptStr(a, "template") is string tpl)
        {
            View? template = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => v.IsTemplate && v.Name.Equals(tpl, StringComparison.OrdinalIgnoreCase));
            if (template == null) notes.Add($"no template '{tpl}'");
            else view.ViewTemplateId = template.Id;
        }
        if (view is View3D && OptStr(a, "display") is string ds)
            view.DisplayStyle = ds.ToLowerInvariant() switch
            {
                "hidden" => DisplayStyle.HLR, "shaded" => DisplayStyle.Shading, "realistic" => DisplayStyle.Realistic,
                _ => DisplayStyle.ShadingWithEdges
            };
        t.Commit();
        return new JsonObject
        {
            ["view"] = view.Name, ["id"] = view.Id.ToString(), ["kind"] = view.ViewType.ToString(), ["scale"] = view.Scale,
            ["note"] = string.Join("; ", notes)
        };
    }

    // ------------------------------------------------------------------ crop_view

    private static JsonObject CropView(Document doc, JsonObject a)
    {
        View view = FindView(doc, Str(a, "view"));
        if (view is View3D or ViewSheet or ViewSchedule) throw new McpError("crop_view crops plans, sections, elevations and drafting views.");
        string to = (OptStr(a, "to") ?? "model").ToLowerInvariant();
        double scale = Math.Max(1, view.Scale);
        double margin = DrwUnits.FromMillimeters(((double?)a["margin_mm"] ?? 10) * scale);

        BoundingBoxXYZ crop = view.CropBox;
        Transform inv = crop.Transform.Inverse;
        var pts = new List<XYZ>();
        void addBox(BoundingBoxXYZ? b)
        {
            if (b == null) return;
            Transform tf = b.Transform ?? Transform.Identity;
            foreach (double x in new[] { b.Min.X, b.Max.X })
                foreach (double y in new[] { b.Min.Y, b.Max.Y })
                    foreach (double z in new[] { b.Min.Z, b.Max.Z })
                        pts.Add(inv.OfPoint(tf.OfPoint(new XYZ(x, y, z))));
        }

        if (a["box_m"] is JsonObject bm)
        {
            XYZ mn = M2(bm["min"]), mx = M2(bm["max"]);
            addBox(new BoundingBoxXYZ { Min = mn, Max = mx });
        }
        else if (to == "elements")
        {
            foreach (string key in StrList(a, "elements"))
                if (long.TryParse(key, out long id) && doc.GetElement(new ElementId(id)) is Element e) addBox(e.get_BoundingBox(view) ?? e.get_BoundingBox(null));
        }
        else
        {
            bool grids = to == "grids" || (bool?)a["include_grids"] == true;
            if (to != "grids")
            {
                // Every model element the view draws (rooms when asked), not annotation, markers or cameras.
                foreach (Element e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
                {
                    Category? c = e.Category;
                    if (c == null) continue;
                    bool rooms = c.BuiltInCategory == BuiltInCategory.OST_Rooms;
                    if (to == "rooms" ? !rooms : (c.CategoryType != CategoryType.Model || rooms)) continue;
                    if (e is ElevationMarker || c.BuiltInCategory == BuiltInCategory.OST_Cameras) continue;
                    addBox(e.get_BoundingBox(view));
                }
            }
            if (grids)
                foreach (Grid g in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>())
                    foreach (Curve c in g.GetCurvesInView(DatumExtentType.ViewSpecific, view))
                    {
                        pts.Add(inv.OfPoint(c.GetEndPoint(0)));
                        pts.Add(inv.OfPoint(c.GetEndPoint(1)));
                    }
        }
        if (pts.Count == 0) throw new McpError($"Nothing to crop '{view.Name}' to (to: {to}).");

        double minX = pts.Min(p => p.X) - margin, maxX = pts.Max(p => p.X) + margin;
        double minY = pts.Min(p => p.Y) - margin, maxY = pts.Max(p => p.Y) + margin;
        using Transaction t = new Transaction(doc, "Crop " + view.Name);
        t.Start();
        var box = new BoundingBoxXYZ
        {
            Transform = crop.Transform,
            Min = new XYZ(minX, minY, crop.Min.Z),
            Max = new XYZ(maxX, maxY, crop.Max.Z)
        };
        view.CropBoxActive = true;
        view.CropBox = box;
        view.CropBoxVisible = (bool?)a["show_crop"] ?? false;
        string annotation = "";
        if ((double?)a["annotation_mm"] is double am)
        {
            Parameter? p = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
            if (p != null && !p.IsReadOnly)
            {
                p.Set(1);
                double off = DrwUnits.FromMillimeters(Math.Max(1, am));   // paper offset
                ViewCropRegionShapeManager sm = view.GetCropRegionShapeManager();
                try
                {
                    sm.LeftAnnotationCropOffset = off; sm.RightAnnotationCropOffset = off;
                    sm.TopAnnotationCropOffset = off; sm.BottomAnnotationCropOffset = off;
                    annotation = $"annotation crop {am:0} mm";
                }
                catch (Exception ex) { annotation = "annotation crop on, offsets refused: " + ex.Message; }
            }
        }
        t.Commit();
        double w = (maxX - minX) * 304.8 / scale, h = (maxY - minY) * 304.8 / scale;
        return new JsonObject
        {
            ["view"] = view.Name, ["to"] = a["box_m"] != null ? "box" : to,
            ["paper_mm"] = $"{w:0} x {h:0} at 1:{scale}", ["note"] = annotation
        };
    }

    // ------------------------------------------------------------------ text and 2D drawing

    /// <summary>Model feet per paper mm in a view (a sheet is 1:1).</summary>
    private static double PaperToModel(View view) => DrwUnits.FromMillimeters(view is ViewSheet ? 1 : Math.Max(1, view.Scale));

    /// <summary>A text type of this font, size and weight, made once and reused ("KH Arial 2.5 B").</summary>
    private static TextNoteType TextType(Document doc, string font, double sizeMm, bool bold)
    {
        string name = $"KH {font} {sizeMm:0.##}{(bold ? " B" : "")}";
        List<TextNoteType> all = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
        TextNoteType? hit = all.FirstOrDefault(x => x.Name == name);
        if (hit != null) return hit;
        TextNoteType made = (TextNoteType)all[0].Duplicate(name);
        made.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set(font);
        made.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(DrwUnits.FromMillimeters(sizeMm));
        made.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD)?.Set(bold ? 1 : 0);
        made.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(1);   // transparent
        made.get_Parameter(BuiltInParameter.LEADER_OFFSET_SHEET)?.Set(0);
        return made;
    }

    private static TextNote? PutText(Document doc, View view, double xMm, double yMm, string text, TextNoteType type,
        double? widthMm, string align, double angleDeg, double k)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var opt = new TextNoteOptions(type.Id)
        {
            HorizontalAlignment = align switch
            {
                "center" => HorizontalTextAlignment.Center, "right" => HorizontalTextAlignment.Right, _ => HorizontalTextAlignment.Left
            },
            Rotation = angleDeg * Math.PI / 180
        };
        var at = new XYZ(xMm * k, yMm * k, 0);
        TextNote note = TextNote.Create(doc, view.Id, at, text, opt);
        if (widthMm is > 0) { try { note.Width = DrwUnits.FromMillimeters(widthMm.Value); } catch { /* below Revit's minimum */ } }
        return note;
    }

    private static readonly Dictionary<int, double> PenMm = new() { [1] = 0.10, [2] = 0.18, [3] = 0.25, [4] = 0.35, [5] = 0.50, [6] = 0.70 };

    /// <summary>A line style "KH Pen n" (n = 1..6), whose projection weight is pen n; made once.</summary>
    private static GraphicsStyle PenStyle(Document doc, int pen)
    {
        Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        string name = "KH Pen " + pen;
        Category sub = lines.SubCategories.Contains(name) ? lines.SubCategories.get_Item(name)
            : doc.Settings.Categories.NewSubcategory(lines, name);
        sub.SetLineWeight(pen, GraphicsStyleType.Projection);
        return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
    }

    private static int PenFor(double widthMm) => PenMm.Where(kv => widthMm <= kv.Value + 1e-6).Select(kv => kv.Key).DefaultIfEmpty(6).First();

    private static JsonObject PlaceText(Document doc, JsonObject a)
    {
        View view = FindView(doc, Str(a, "view"));
        double k = PaperToModel(view);
        string font = OptStr(a, "font") ?? "TH Sarabun New";
        int made = 0, lines = 0;
        using Transaction t = new Transaction(doc, "Text in " + view.Name);
        t.Start();
        if (a["items"] is JsonArray items)
            foreach (JsonObject it in items.OfType<JsonObject>())
            {
                TextNoteType type = TextType(doc, OptStr(it, "font") ?? font, (double?)it["size_mm"] ?? (double?)a["size_mm"] ?? 2.5,
                    (bool?)it["bold"] ?? false);
                if (PutText(doc, view, (double)it["x"]!, (double)it["y"]!, (string?)it["text"] ?? "", type, (double?)it["width_mm"],
                        OptStr(it, "align") ?? "left", (double?)it["angle"] ?? 0, k) != null) made++;
            }
        if (a["lines"] is JsonArray ls)
            foreach (JsonArray l in ls.OfType<JsonArray>())
            {
                // [x1, y1, x2, y2] or [x1, y1, x2, y2, width_mm]
                var p0 = new XYZ((double)l[0]! * k, (double)l[1]! * k, 0);
                var p1 = new XYZ((double)l[2]! * k, (double)l[3]! * k, 0);
                if (p0.DistanceTo(p1) < doc.Application.ShortCurveTolerance) continue;
                DetailCurve dc = doc.Create.NewDetailCurve(view, Line.CreateBound(p0, p1));
                dc.LineStyle = PenStyle(doc, PenFor(l.Count > 4 ? (double)l[4]! : 0.18));
                lines++;
            }
        t.Commit();
        return new JsonObject { ["view"] = view.Name, ["texts"] = made, ["lines"] = lines };
    }

    /// <summary>
    /// Draws a JSON file of 2D vectors (from the PDF extractor) as detail lines, filled regions and
    /// text. File: { "lines": [[x1,y1,x2,y2,w]], "polylines": [{"pts":[[x,y],...],"w":0.25,"closed":false}],
    /// "fills": [{"pts":[[x,y],...]}], "arcs": [{"c":[x,y],"r":5,"a0":0,"a1":90,"w":0.18}],
    /// "texts": [{"t":"1.00","x":0,"y":0,"size":2.5,"angle":0,"align":"center","bold":false}] }, all paper mm, y up.
    /// </summary>
    private static JsonObject ImportVectors(Document doc, JsonObject a)
    {
        View view = FindView(doc, Str(a, "view"));
        string file = Str(a, "file");
        if (!File.Exists(file)) throw new McpError($"No file '{file}'.");
        JsonObject data = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new McpError("The file is not a JSON object.");
        double k = PaperToModel(view);
        double ox = 0, oy = 0;
        if (a["origin_mm"] is JsonArray o && o.Count == 2) { ox = (double)o[0]!; oy = (double)o[1]!; }
        XYZ P(double x, double y) => new XYZ((x + ox) * k, (y + oy) * k, 0);
        string font = OptStr(a, "font") ?? "TH Sarabun New";
        double tol = doc.Application.ShortCurveTolerance;
        int lines = 0, fills = 0, texts = 0, tooShort = 0, failed = 0;
        var styles = new Dictionary<int, GraphicsStyle>();
        GraphicsStyle Style(double w) { int pen = PenFor(w); return styles.TryGetValue(pen, out var s) ? s : styles[pen] = PenStyle(doc, pen); }

        using Transaction t = new Transaction(doc, "Vectors in " + view.Name);
        t.Start();
        void Seg(XYZ p0, XYZ p1, double w)
        {
            if (p0.DistanceTo(p1) < tol) { tooShort++; return; }
            try { DetailCurve dc = doc.Create.NewDetailCurve(view, Line.CreateBound(p0, p1)); dc.LineStyle = Style(w); lines++; }
            catch { failed++; }
        }
        if (data["lines"] is JsonArray ls)
            foreach (JsonArray l in ls.OfType<JsonArray>())
                Seg(P((double)l[0]!, (double)l[1]!), P((double)l[2]!, (double)l[3]!), l.Count > 4 ? (double)l[4]! : 0.18);
        if (data["polylines"] is JsonArray pls)
            foreach (JsonObject pl in pls.OfType<JsonObject>())
            {
                var pts = (pl["pts"] as JsonArray ?? new JsonArray()).OfType<JsonArray>().Select(q => P((double)q[0]!, (double)q[1]!)).ToList();
                double w = (double?)pl["w"] ?? 0.18;
                for (int i = 0; i + 1 < pts.Count; i++) Seg(pts[i], pts[i + 1], w);
                if ((bool?)pl["closed"] == true && pts.Count > 2) Seg(pts[pts.Count - 1], pts[0], w);
            }
        if (data["arcs"] is JsonArray arcs)
            foreach (JsonObject ar in arcs.OfType<JsonObject>())
            {
                var c = (JsonArray)ar["c"]!;
                double r = (double)ar["r"]! * k, a0 = (double?)ar["a0"] ?? 0, a1 = (double?)ar["a1"] ?? 360;
                if (r < tol) { tooShort++; continue; }
                try
                {
                    Curve arc = Math.Abs(a1 - a0) >= 359.9
                        ? Arc.Create(P((double)c[0]!, (double)c[1]!), r, 0, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY)
                        : Arc.Create(P((double)c[0]!, (double)c[1]!), r, a0 * Math.PI / 180, a1 * Math.PI / 180, XYZ.BasisX, XYZ.BasisY);
                    DetailCurve dc = doc.Create.NewDetailCurve(view, arc);
                    dc.LineStyle = Style((double?)ar["w"] ?? 0.18);
                    lines++;
                }
                catch { failed++; }
            }
        string fillNote = "";
        if (data["fills"] is JsonArray fs && fs.Count > 0)
        {
            if (view is ViewSheet) fillNote = "fills skipped: Revit draws no filled region on a sheet";
            else
            {
                ElementId fillType = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                    .OrderByDescending(f => f.Name.IndexOf("Solid", StringComparison.OrdinalIgnoreCase) >= 0).First().Id;
                foreach (JsonObject f in fs.OfType<JsonObject>())
                {
                    var pts = (f["pts"] as JsonArray ?? new JsonArray()).OfType<JsonArray>().Select(q => P((double)q[0]!, (double)q[1]!)).ToList();
                    if (pts.Count < 3) continue;
                    try
                    {
                        var loop = new CurveLoop();
                        for (int i = 0; i < pts.Count; i++)
                        {
                            XYZ p0 = pts[i], p1 = pts[(i + 1) % pts.Count];
                            if (p0.DistanceTo(p1) >= tol) loop.Append(Line.CreateBound(p0, p1));
                        }
                        FilledRegion.Create(doc, fillType, view.Id, new List<CurveLoop> { loop });
                        fills++;
                    }
                    catch { failed++; }
                }
            }
        }
        if (data["texts"] is JsonArray ts)
            foreach (JsonObject tx in ts.OfType<JsonObject>())
            {
                TextNoteType type = TextType(doc, OptStr(tx, "font") ?? font, Math.Round(((double?)tx["size"] ?? 2.5) * 4) / 4, (bool?)tx["bold"] ?? false);
                try
                {
                    if (PutText(doc, view, (double)tx["x"]! + ox, (double)tx["y"]! + oy, (string?)tx["t"] ?? "", type, null,
                            OptStr(tx, "align") ?? "left", (double?)tx["angle"] ?? 0, k) != null) texts++;
                }
                catch { failed++; }
            }
        t.Commit();
        return new JsonObject
        {
            ["view"] = view.Name, ["lines"] = lines, ["fills"] = fills, ["texts"] = texts,
            ["too_short_skipped"] = tooShort, ["failed"] = failed, ["note"] = fillNote
        };
    }

    // ------------------------------------------------------------------ pdf_underlay

    /// <summary>
    /// A PDF page as an image in a view at its true scale: pdf point 'pdf_mm' (from the page's top-left,
    /// y down, as the extractor gives it) lands on model point 'model_m'. A second pair reports the error.
    /// </summary>
    private static JsonObject PdfUnderlay(Document doc, JsonObject a)
    {
        View view = FindView(doc, Str(a, "view"));
        string pdf = Str(a, "pdf");
        if (!File.Exists(pdf)) throw new McpError($"No file '{pdf}'.");
        int page = (int?)a["page"] ?? 1;
        double drawingScale = (double?)a["scale"] ?? throw new McpError("'scale' is the PDF drawing's scale, e.g. 75 for 1:75.");
        int dpi = (int?)a["dpi"] ?? 150;
        if (a["pdf_mm"] is not JsonArray pm || pm.Count < 1 || a["model_m"] is not JsonArray mm || mm.Count != pm.Count)
            throw new McpError("Give 'pdf_mm' and 'model_m' as matching lists of points: [[x,y], ...].");

        using Transaction t = new Transaction(doc, "PDF underlay in " + view.Name);
        t.Start();
        var opts = new ImageTypeOptions(pdf, false, ImageTypeSource.Import) { PageNumber = page, Resolution = dpi };
        ImageType type = ImageType.Create(doc, opts);
        double pageWmm = type.WidthInPixels / (double)dpi * 25.4, pageHmm = type.HeightInPixels / (double)dpi * 25.4;
        double s = DrwUnits.FromMillimeters(drawingScale);   // model feet per pdf mm
        XYZ m0 = M2(mm[0]);
        var p0 = (JsonArray)pm[0]!;
        XYZ topLeft = m0 - new XYZ((double)p0[0]! * s, -(double)p0[1]! * s, 0);
        XYZ centre = topLeft + new XYZ(pageWmm * s / 2, -pageHmm * s / 2, 0);
        ImageInstance img = ImageInstance.Create(doc, view, type.Id, new ImagePlacementOptions(centre, BoxPlacement.Center));
        img.LockProportions = true;
        img.Width = pageWmm * s;
        img.SetLocation(centre, BoxPlacement.Center);
        try { img.DrawLayer = DrawLayer.Background; } catch { /* not in this view kind */ }
        t.Commit();

        string check = "";
        if (pm.Count > 1)
        {
            var p1 = (JsonArray)pm[1]!;
            XYZ at = topLeft + new XYZ((double)p1[0]! * s, -(double)p1[1]! * s, 0);
            XYZ want = M2(mm[1]);
            check = $"second point off by {at.DistanceTo(want) * 304.8:0} mm";
        }
        return new JsonObject
        {
            ["view"] = view.Name, ["image"] = img.Id.ToString(), ["page_mm"] = $"{pageWmm:0} x {pageHmm:0}", ["check"] = check
        };
    }
}
#endif
