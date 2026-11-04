using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KhBatchRunner.Commands;
using KhBatchRunner.Drw.Dialogs;

namespace KhBatchRunner.Drw.DoorWindow;

/// <summary>
/// What the Enlarge Door &amp; Window window asks of the model. Runs only where the Revit API may
/// be used: inside the command, or from <see cref="DoorWindowSession"/>'s external event once the
/// window is open beside Revit.
/// </summary>
internal sealed class DoorWindowModel : IDoorWindowApi
{
    private readonly UIApplication _app;
    private readonly Document _doc;
    private List<DwItem> _items = new();

    public DoorWindowModel(UIApplication app, Document doc)
    {
        _app = app;
        _doc = doc;
    }

    public Document Document => _doc;

    /// <summary>Doors and windows both, as the Items tab lists them (each set filters its own).</summary>
    public List<DwItemRow> Items(DoorWindowPreset p)
    {
        var warnings = new List<string>();
        _items = DoorWindowItems.Collect(_doc, true, true, warnings).Concat(DoorWindowItems.Collect(_doc, false, true, warnings)).ToList();
        Dictionary<string, double> adjusted = DwDefinitions.Widths(_doc, p.SetKey);
        Dictionary<string, DwDim> dims = DwDefinitions.Dims(_doc, p.SetKey);
        return _items.Select(i => new DwItemRow
        {
            Mark = i.Mark.Length > 0 ? i.Mark : "(no Type Mark)",
            Category = i.IsDoor ? "Door" : "Window",
            // In the project's units, as the schedule's Width x Height field writes it.
            Size = i.Width > 0 && i.Height > 0 ? DoorWindowItems.Size(_doc, i.Width, i.Height, DwSizeFormat.ProjectUnits) : "?",
            Qty = i.Instances.Count,
            Types = string.Join(", ", i.Types.Select(DoorWindowItems.TypeName)),
            Warnings = string.Join(" ", i.Warnings),
            WidthMm = i.Width * 304.8,
            HeightSillMm = (i.Height + i.Sill) * 304.8,
            InstanceSized = i.InstanceSized,
            AdjustedWidthMm = adjusted.TryGetValue(i.Key, out double a) ? a : 0,
            MovedDims = dims.Count(d => d.Value.Moved && d.Key.StartsWith(i.Key + "#", StringComparison.Ordinal))
        }).ToList();
    }

    public DwTitleBlockData? TitleBlock(string name) => DoorWindowSheets.ReadOne(_doc, name);

    /// <summary>What each item draws, model mm: plan width (wall stubs included); elevation width;
    /// elevation height with the sill (the larger of drawn and nominal); and the text fit. Measured on
    /// legend components placed and rolled back.</summary>
    public DwMeasured Measure(DoorWindowPreset p)
    {
        var m = new DwMeasured();
        if (_items.Count == 0 || LegendBuilder.Source(_doc) is not { } source) return m;
        using var t = new Transaction(_doc, "Measure doors & windows");
        t.Start();
        try
        {
            Dictionary<ElementId, DwExtent> drawn = LegendBuilder.Extents(_doc, source.Legend, source.Comp, _items, p, out double fit);
            m.TextFit = fit;
            foreach (DwItem i in _items)
                if (drawn.TryGetValue(i.Primary.Id, out DwExtent e))
                    m.Extents[i.Key] = (e.PlanWidth * 304.8, e.ElevationWidth * 304.8, (Math.Max(i.Height, e.ElevationHeight) + i.Sill) * 304.8);
        }
        finally { t.RollBack(); }
        return m;
    }

    /// <summary>Forgets the set's hand edits (cell widths, moved dimensions) and rebuilds it.</summary>
    public string ResetWidths(DoorWindowPreset p) =>
        LegendBuilder.ForgetHandSetWidths(_doc, p) ? Build(p) : "Not built in this model yet: nothing to reset.";

    public List<DwSheetParameterChoice> SheetParameters() => DoorWindowSheets.ParameterChoices(_doc);

    public DwScheduleData? Schedule(DoorWindowPreset p) => DwSchedule.Find(_doc, p) is { } s ? DwSchedule.Read(s) : null;

    /// <summary>The set's own schedule, made from the starter if it has none (or put back in
    /// shape), opened as the active view so the user can edit it, and read.</summary>
    public DwScheduleData? EnsureAndOpen(DoorWindowPreset p, List<string> did)
    {
        ViewSchedule s;
        using (var t = new Transaction(_doc, "Door & window schedule"))
        {
            t.Start();
            s = DwSchedule.Ensure(_doc, p, did);
            if (did.Count > 0) t.Commit(); else t.RollBack();
        }
        s = DwSchedule.Find(_doc, p) ?? s;
        // RequestViewChange, not ActiveView =: setting ActiveView is refused from the command's and
        // the external event's context (the new schedule did not open, 2026-10-21); the request is
        // carried out as soon as Revit is free.
        try { if (_app.ActiveUIDocument?.Document.Equals(_doc) == true && _app.ActiveUIDocument.ActiveView?.Id != s.Id) _app.ActiveUIDocument.RequestViewChange(s); }
        catch (Exception ex) { Diagnostics.RunTrace.Mark("Door & Window: schedule not opened: " + ex.Message); }
        return DwSchedule.Read(s);
    }

    public string Build(DoorWindowPreset p)
    {
        List<IReadOnlyDictionary<string, string>> rows = LegendBuilder.Run(_doc, p);
        if (_app.ActiveUIDocument is { } ui && ui.Document.Equals(_doc)) DrwResults.Show("Enlarge Door & Window", ui, LegendBuilder.Columns, rows);
        int failed = rows.Count(r => r["Status"] == "Failed");
        int created = rows.Count(r => r["Status"] == "Created");
        return failed > 0 ? $"Built with {failed} failure(s): " + rows.First(r => r["Status"] == "Failed")["Note"]
            : $"Built: {created} item(s) / legend(s) / sheet(s) made.";
    }

    public string ApplyNow() => LiveDoorWindow.ApplyNow(_doc);

    /// <summary>Saves a row's "Value from" on the set's schedule (everyone's legends follow it), and
    /// reads the schedule again.</summary>
    public DwScheduleData? SetValueFrom(DoorWindowPreset p, long fieldId, string choice)
    {
        if (DwSchedule.Find(_doc, p) is not { } s) return null;
        using (var t = new Transaction(_doc, "Door & window: value from"))
        {
            t.Start();
            DwValueFrom.Set(s, fieldId, choice);
            t.Commit();
        }
        LiveDoorWindow.Watch(_doc);
        return DwSchedule.Read(s);
    }

    public bool SetLive(DoorWindowPreset p, bool live) => LiveDoorWindow.SetLive(_doc, p, live);

    /// <summary>Adds or removes the schedule's Size (Width x Height) and Count fields, and reads it again.</summary>
    public (DwScheduleData? Schedule, List<string> Did) SetSizeQty(DoorWindowPreset p, bool size, bool qty)
    {
        if (DwSchedule.Find(_doc, p) is not { } s) return (null, new List<string>());
        List<string> did;
        using (var t = new Transaction(_doc, "Door & window: Size and Qty fields"))
        {
            t.Start();
            did = DwSchedule.SetSizeQty(s, size, p.SizeLabel, qty, p.QtyLabel);
            t.Commit();
        }
        LiveDoorWindow.Watch(_doc);
        return (DwSchedule.Read(s), did);
    }
}
