namespace KhBatchRunner.Drw.DoorWindow;

/// <summary>
/// Where each item sits, in paper millimetres. One cell per item: header, elevation box, plan box
/// (or plan first), then the table rows. Every cell in a sheet's grid takes the tallest cell's
/// height, so rows of cells line up across the sheet as on a hand-drawn A-12.
/// </summary>
/// <summary>What a type's legend components draw (feet): plan width; elevation width (twice its
/// larger reach from the nominal centre) and height above the nominal bottom.</summary>
internal readonly record struct DwExtent(double PlanWidth, double ElevationWidth, double ElevationHeight);

internal static class DoorWindowLayout
{
    /// <summary>Height of one item's cell for the given lines per row.</summary>
    /// <param name="rowLineMm">One table line on paper: the preset's, or the row family's own Line
    /// Height divided by the legend scale.</param>
    public static double CellHeightMm(DoorWindowPreset p, IEnumerable<int> rowLines, double rowLineMm) =>
        p.ElevationBoxMm + p.PlanBoxMm + p.HeaderMm + rowLines.Sum(l => Math.Max(1, l)) * rowLineMm;

    /// <summary>How many columns and rows of cells a drawing area of <paramref name="widthMm"/> x
    /// <paramref name="heightMm"/> takes. Never less than one of each: an item bigger than the area
    /// still gets a sheet of its own, and the preview says it overflows.</summary>
    public static (int Columns, int Rows) Grid(double widthMm, double heightMm, double cellWidthMm, double cellHeightMm) =>
        (Math.Max(1, (int)Math.Floor(widthMm / cellWidthMm + 1e-6)),
         Math.Max(1, (int)Math.Floor(heightMm / cellHeightMm + 1e-6)));

    /// <summary>Items split into sheets: how many on each, in order.</summary>
    public static List<int> Sheets(int items, int perSheet)
    {
        var sheets = new List<int>();
        for (int left = items; left > 0; left -= perSheet) sheets.Add(Math.Min(left, perSheet));
        return sheets;
    }

    /// <summary>"11 items → 2 sheets (8 + 3)".</summary>
    public static string Summary(int items, List<int> sheets) =>
        $"{items} item{(items == 1 ? "" : "s")} → {sheets.Count} sheet{(sheets.Count == 1 ? "" : "s")}" +
        (sheets.Count > 1 ? $" ({PerSheet(sheets)})" : "");

    /// <summary>Items per sheet, runs of the same count shortened: "3 + 3 + 2", or "10 × 3 + 2" once a
    /// run is longer than two (32 sheets of 1 read "1 + 1 + 1 …" past the end of the line).</summary>
    public static string PerSheet(List<int> sheets)
    {
        var parts = new List<string>();
        for (int i = 0; i < sheets.Count;)
        {
            int j = i;
            while (j < sheets.Count && sheets[j] == sheets[i]) j++;
            int run = j - i;
            if (run > 2) parts.Add($"{run} × {sheets[i]}");
            else parts.AddRange(Enumerable.Repeat(sheets[i].ToString(), run));
            i = j;
        }
        return string.Join(" + ", parts);
    }

    /// <summary>Top-left corner of cell <paramref name="index"/> on its sheet, columns first, in mm
    /// from the grid's top-left (y grows downward).</summary>
    public static (double X, double Y) CellOrigin(int index, int columns, double cellWidthMm, double cellHeightMm) =>
        (index % columns * cellWidthMm, index / columns * cellHeightMm);

    /// <summary>How many cells an item needs side by side: its width plus room for the height
    /// dimension and its text on each side, in cells of the preset width.</summary>
    public static int Span(double itemWidthMm, DoorWindowPreset p, int columns) =>
        Math.Max(1, Math.Min(Math.Max(1, columns), (int)Math.Ceiling((itemWidthMm + 2 * (p.DimOffsetMm + 5)) / Math.Max(1, p.CellWidthMm) - 1e-6)));

    /// <summary>The least width an item's cell can have, paper mm: the nominal width and what the
    /// elevation draws, each with room for the height dimension and its text either side; the plan
    /// as drawn (wall stubs included) with a little air.</summary>
    public static double NeedWidthMm(double widthMm, double planWidthMm, double elevationWidthMm, DoorWindowPreset p)
    {
        double dims = 2 * (p.DimOffsetMm + 5);
        return Math.Max(Math.Max(widthMm + dims, elevationWidthMm + dims), planWidthMm + 4);
    }

    /// <summary>
    /// An item's cell width, paper mm: whole cells of the preset width, as many as it needs (no more
    /// than fit across), or the width someone gave its cell by hand (<paramref name="adjustedMm"/>,
    /// remembered by the build). A hand-set width is kept even below what the item needs (the user,
    /// 2026-10-22: raising it back read as "cannot adjust the cell"); <paramref name="spills"/> then
    /// says the drawing may cross the cell's edge.
    /// </summary>
    public static double CellWidthMm(double needMm, double? adjustedMm, DoorWindowPreset p, int columns, out bool spills)
    {
        double cell = Math.Max(1, p.CellWidthMm);
        spills = false;
        if (adjustedMm is double a && a > 0)
        {
            spills = a < needMm - 0.5;
            return a;
        }
        int span = Math.Max(1, Math.Min(Math.Max(1, columns), (int)Math.Ceiling(needMm / cell - 1e-6)));
        return span * cell;
    }

    /// <summary>The value column of a cell this wide, paper mm: what is left of the cell past the
    /// label column and the gap, less a margin at the cell's right edge. Values wrap at it.</summary>
    public static double ValueWidthMm(double cellWidthMm, DoorWindowPreset p) =>
        Math.Max(10, cellWidthMm - p.LabelWidthMm - p.TextInsetMm - 3);

    /// <summary>The elevation box an item needs: its height and sill, the floor line below, the
    /// width dimension and its text above (~4 mm), and the title (~5 mm).</summary>
    public static double ElevationNeedMm(double itemHeightAndSillMm, DoorWindowPreset p) =>
        Math.Ceiling(itemHeightAndSillMm + p.FloorGapMm + p.DimOffsetMm + 4 + 5);

    /// <summary>
    /// Lays items out left to right, each cell as wide as its <see cref="DwNeed.WidthMm"/>, in rows
    /// as wide as <paramref name="columns"/> cells of the preset width: a cell that does not fit in
    /// what is left of a row starts the next. Cells of whole cell widths line up as a grid; a cell
    /// whose width was set by hand moves the ones after it along (the user, 2026-10-22).
    /// Each row of cells takes the elevation box its tallest item needs (never less than the
    /// preset's) and the tallest table in it, so cells line up across the row. A sheet (one legend)
    /// takes rows until the next would pass <paramref name="maxHeightMm"/> (0 = no limit), or
    /// <see cref="DoorWindowPreset.RowsPerLegend"/> rows when there is no height to fill.
    /// </summary>
    public static DwPack Pack(IReadOnlyList<DwNeed> needs, DoorWindowPreset p, int columns, double maxHeightMm)
    {
        columns = Math.Max(1, columns);
        double rowWidth = columns * Math.Max(1, p.CellWidthMm);
        var pack = new DwPack();
        var row = new List<(int Index, int Col, int Span, double X, double Width)>();
        double x = 0;
        double sheetUsed = 0;

        void CloseRow()
        {
            if (row.Count == 0) return;
            double elev = Math.Max(p.ElevationBoxMm, row.Max(r => needs[r.Index].ElevationMm));
            double table = row.Max(r => needs[r.Index].TableMm);
            double height = elev + p.PlanBoxMm + p.HeaderMm + table;
            // A new sheet when this row would pass the height (a sheet always takes at least one
            // row, even one taller than the sheet), or when the sheet has its rows already.
            bool newSheet = pack.Rows.Count == 0
                || (maxHeightMm > 0 ? sheetUsed > 0 && sheetUsed + height > maxHeightMm + 1e-6
                                    : pack.Rows[pack.Rows.Count - 1].Count >= Math.Max(1, p.RowsPerLegend));
            if (newSheet)
            {
                pack.Rows.Add(new List<DwPackRow>());
                sheetUsed = 0;
            }
            int sheet = pack.Rows.Count - 1;
            pack.Rows[sheet].Add(new DwPackRow(sheetUsed, elev, table, height));
            foreach (var r in row)
                pack.Slots.Add(new DwSlot(r.Index, sheet, pack.Rows[sheet].Count - 1, r.Col, r.Span, r.X, r.Width));
            sheetUsed += height;
            row.Clear();
            x = 0;
        }

        for (int i = 0; i < needs.Count; i++)
        {
            double width = needs[i].WidthMm > 0 ? needs[i].WidthMm : Math.Min(columns, Math.Max(1, needs[i].Span)) * p.CellWidthMm;
            if (row.Count > 0 && x + width > rowWidth + 1e-6) CloseRow();
            row.Add((i, row.Count, Math.Max(1, needs[i].Span), x, width));
            x += width;
        }
        CloseRow();
        pack.Slots.Sort((a, b) => a.Item.CompareTo(b.Item));
        return pack;
    }
}

/// <summary>A title block's drawing frame, paper mm from its lower-left corner.</summary>
internal readonly record struct DwFrame(double L, double B, double R, double T)
{
    public double Width => R - L;
    public double Height => T - B;
}

internal static class DwSheets
{
    /// <summary>Cells across and the height a sheet's grid may fill, inside the frame less the margin.</summary>
    public static (int Columns, double HeightMm) Grid(DwFrame f, DoorWindowPreset p) =>
        (Math.Max(1, (int)Math.Floor((f.Width - 2 * p.SheetMarginMm) / Math.Max(1, p.CellWidthMm) + 1e-6)),
         Math.Max(1, f.Height - 2 * p.SheetMarginMm));

    /// <summary>The <paramref name="index"/>-th sheet number after <paramref name="start"/>: its last
    /// run of digits counts up, keeping its width (A-12, A-13…; A-09, A-10), skipping numbers in
    /// <paramref name="taken"/>. A start with no digits gets " 2", " 3"… after the first.</summary>
    public static string Number(string start, int index, ICollection<string> taken)
    {
        var m = System.Text.RegularExpressions.Regex.Match(start, @"(\d+)(?!.*\d)");
        for (int n = index; ; n++)
        {
            string candidate = m.Success
                ? start.Substring(0, m.Index) + (long.Parse(m.Value) + n).ToString().PadLeft(m.Length, '0') + start.Substring(m.Index + m.Length)
                : n == 0 ? start : $"{start} {n + 1}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}

/// <summary>What one item needs: cells across, elevation box and table height, and its cell's width
/// (0 = <see cref="Span"/> cells of the preset width), paper mm.</summary>
internal readonly record struct DwNeed(int Span, double ElevationMm, double TableMm, double WidthMm = 0);

/// <summary>Where one item went: its sheet (legend), row of cells, place in the row, cells across,
/// and its cell's left edge and width, paper mm from the row's left.</summary>
internal readonly record struct DwSlot(int Item, int Sheet, int Row, int Col, int Span, double XMm, double WidthMm);

/// <summary>A row of cells on a sheet: its top (mm down from the sheet's first row) and sizes.</summary>
internal readonly record struct DwPackRow(double TopMm, double ElevationMm, double TableMm, double HeightMm);

internal sealed class DwPack
{
    public List<DwSlot> Slots { get; } = new();
    /// <summary>Per sheet, its rows of cells, top to bottom.</summary>
    public List<List<DwPackRow>> Rows { get; } = new();
    public int Sheets => Rows.Count;
    public List<int> ItemsPerSheet() => Enumerable.Range(0, Sheets).Select(s => Slots.Count(x => x.Sheet == s)).ToList();
}
