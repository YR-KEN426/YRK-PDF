namespace Yrk.PdfStudio.Core.Pdf.Export;

/// <summary>Something on a page, in reading order; Top, Bottom and Left are points on the page.</summary>
public abstract record LayoutBlock(double Top, double Bottom, double Left);

public sealed record LayoutParagraph(TextParagraph Paragraph) : LayoutBlock(Paragraph.Top, Paragraph.Bottom, Paragraph.Left);

/// <summary>
/// A part of the page to copy as a picture: a photo, a logo, a chart or other drawing (with any text on it).
/// <see cref="IsPhoto"/> suggests JPEG; drawings keep sharper edges as PNG.
/// </summary>
public sealed record LayoutPicture(double X, double Y, double Width, double Height, bool IsPhoto) : LayoutBlock(Y, Y + Height, X)
{
    public double Right => X + Width;
}

[Flags]
public enum CellBorders
{
    None = 0,
    Top = 1,
    Bottom = 2,
    Left = 4,
    Right = 8,
}

/// <summary>A table cell: its place in the grid, what is in it, its border lines and background colour.</summary>
public sealed record LayoutCell(int Row, int Column, int RowSpan, int ColumnSpan, IReadOnlyList<LayoutBlock> Content, CellBorders Borders, PdfColor? Fill);

/// <summary>A table rebuilt from the lines drawn on the page. Cells cover the grid; merged cells span several.</summary>
public sealed record LayoutTable(double X, double Y, IReadOnlyList<double> ColumnWidths, IReadOnlyList<double> RowHeights, IReadOnlyList<LayoutCell> Cells)
    : LayoutBlock(Y, Y + RowHeights.Sum(), X)
{
    public double Width => ColumnWidths.Sum();
}

/// <summary>
/// Rebuilds a page for Word and Excel from its text and graphics: tables from the lines drawn around and between
/// cells (or only above and below rows), cell colours and merged cells, and pictures where the page has photos,
/// logos or drawings. Everything else is paragraphs, as <see cref="TextLayout"/> makes them.
/// </summary>
public static class PageLayout
{
    private const double Tolerance = 2;        // Points: lines this close are the same line, or touch.
    private const double MinOpenTableRule = 100; // Points: lines above and below rows are at least this long.
    private const double CellPadding = 2;

    public static IReadOnlyList<LayoutBlock> Analyze(PdfPageText text, PdfPageGraphics graphics)
    {
        var horizontal = Merge(graphics.Rules.Where(r => r.IsHorizontal), horizontal: true);
        var vertical = Merge(graphics.Rules.Where(r => !r.IsHorizontal), horizontal: false);
        var spans = text.Spans.ToList();
        var shapes = graphics.Graphics.ToList();
        var blocks = new List<LayoutBlock>();

        var tables = GridTables(horizontal, vertical, spans, shapes).Select(t => t.Figure ? t : WithTextBeside(t, spans)).ToList();
        var used = tables.SelectMany(t => t.Rules).ToHashSet();
        tables.AddRange(OpenTables(horizontal.Where(r => !used.Contains(r)).ToList(), graphics.Rules.Where(r => r.IsHorizontal).ToList(), spans, tables));
        var figures = new List<LayoutPicture>();
        foreach (var table in tables)
        {
            if (!table.Figure)
            {
                SplitAtColumns(spans, table);
            }

            var (x, y, right, bottom) = (table.Columns[0], table.Rows[0], table.Columns[^1], table.Rows[^1]);
            bool Inside(double cx, double cy) => cx > x && cx < right && cy > y && cy < bottom;
            var tableSpans = spans.Where(s => Inside(s.X + (s.Width / 2), s.Y + (s.Height / 2))).ToList();
            var tableShapes = shapes.Where(g => Inside(g.X + (g.Width / 2), g.Y + (g.Height / 2))).ToList();
            spans.RemoveAll(tableSpans.Contains);
            shapes.RemoveAll(tableShapes.Contains);
            if (table.Figure)
            {
                figures.Add(new LayoutPicture(x - 1, y - 1, right - x + 2, bottom - y + 2, tableShapes.Any(g => g.Kind == PdfGraphicKind.Image)));
                continue;
            }

            blocks.Add(BuildTable(table, tableSpans, tableShapes, text));
        }

        // Lines outside tables that touch a drawing belong to it (chart axes and grid lines).
        var looseRules = horizontal.Concat(vertical).Where(r => !tables.Any(t => t.Rules.Contains(r))).ToList();
        blocks.AddRange(figures);
        blocks.AddRange(Flow(spans, shapes, looseRules, text, 0, text.Width, null));
        return [.. blocks.OrderBy(b => b.Top).ThenBy(b => b.Left)];
    }

    // --- Lines ------------------------------------------------------------------------------------

    /// <summary>Joins pieces of the same line (tables are often drawn one cell side at a time).</summary>
    private static List<PdfRule> Merge(IEnumerable<PdfRule> rules, bool horizontal)
    {
        double Position(PdfRule r) => horizontal ? r.Y1 : r.X1;
        double Start(PdfRule r) => horizontal ? r.X1 : r.Y1;
        double End(PdfRule r) => horizontal ? r.X2 : r.Y2;

        var merged = new List<PdfRule>();
        foreach (var rule in rules.OrderBy(Position).ThenBy(Start))
        {
            var index = merged.FindLastIndex(m => Math.Abs(Position(m) - Position(rule)) <= Tolerance / 2 && Start(rule) <= End(m) + Tolerance);
            if (index < 0)
            {
                merged.Add(rule);
                continue;
            }

            var m = merged[index];
            merged[index] = horizontal
                ? m with { X2 = Math.Max(m.X2, rule.X2) }
                : m with { Y2 = Math.Max(m.Y2, rule.Y2) };
        }

        return merged;
    }

    private static bool Crosses(PdfRule h, PdfRule v) =>
        v.X1 >= h.X1 - Tolerance && v.X1 <= h.X2 + Tolerance && h.Y1 >= v.Y1 - Tolerance && h.Y1 <= v.Y2 + Tolerance;

    /// <summary>True when lines cover most of the segment from <paramref name="from"/> to <paramref name="to"/> at <paramref name="position"/>.</summary>
    private static bool Covered(IEnumerable<PdfRule> rules, bool horizontal, double position, double from, double to)
    {
        var need = (to - from) * 0.6;
        return rules.Any(r =>
            r.IsHorizontal == horizontal
            && Math.Abs((horizontal ? r.Y1 : r.X1) - position) <= Tolerance
            && Math.Min(horizontal ? r.X2 : r.Y2, to) - Math.Max(horizontal ? r.X1 : r.Y1, from) >= need);
    }

    private static List<double> Edges(IEnumerable<double> positions)
    {
        var edges = new List<double>();
        foreach (var p in positions.Order())
        {
            if (edges.Count == 0 || p - edges[^1] > Tolerance * 1.5)
            {
                edges.Add(p);
            }
        }

        return edges;
    }

    // --- Tables -----------------------------------------------------------------------------------

    /// <summary>A table found on the page: grid edges, the lines that drew it, and whether rows were found from text.</summary>
    /// <remarks>A <see cref="Figure"/> is a frame around a chart or picture: it becomes one picture, its text included.</remarks>
    /// <remarks><see cref="LooseFrom"/>: columns from this index on were added for text beside the lines (no cell merging there).</remarks>
    private sealed record FoundTable(List<double> Columns, List<double> Rows, List<PdfRule> Rules, bool Open, bool Figure = false, int LooseFrom = int.MaxValue);

    /// <summary>Tables drawn with lines on all sides: connected horizontal and vertical lines making at least two cells.</summary>
    private static List<FoundTable> GridTables(List<PdfRule> horizontal, List<PdfRule> vertical, List<PdfTextSpan> spans, List<PdfGraphic> shapes)
    {
        // Connected groups of crossing lines.
        var all = horizontal.Concat(vertical).ToList();
        var parent = Enumerable.Range(0, all.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (var i = 0; i < horizontal.Count; i++)
        {
            for (var j = 0; j < vertical.Count; j++)
            {
                if (Crosses(horizontal[i], vertical[j]))
                {
                    parent[Find(i)] = Find(horizontal.Count + j);
                }
            }
        }

        var tables = new List<FoundTable>();
        foreach (var group in Enumerable.Range(0, all.Count).GroupBy(Find))
        {
            var rules = group.Select(i => all[i]).ToList();
            var h = rules.Where(r => r.IsHorizontal).ToList();
            var v = rules.Where(r => !r.IsHorizontal).ToList();
            if (h.Count < 2 || v.Count < 2)
            {
                continue;
            }

            var rows = Edges(h.Select(r => r.Y1).Concat([v.Min(r => r.Y1), v.Max(r => r.Y2)]));
            var columns = Edges(v.Select(r => r.X1).Concat([h.Min(r => r.X1), h.Max(r => r.X2)]));
            if (rows.Count < 2 || columns.Count < 2)
            {
                continue;
            }

            var (x, y, right, bottom) = (columns[0], rows[0], columns[^1], rows[^1]);
            var area = (right - x) * (bottom - y);
            var drawn = shapes
                .Where(g => g.Kind == PdfGraphicKind.Drawing && g.X >= x - Tolerance && g.Right <= right + Tolerance && g.Y >= y - Tolerance && g.Bottom <= bottom + Tolerance)
                .Where(g => g.Fill is null || !CoversCell(g, columns, rows))
                .Sum(g => g.Width * g.Height);
            var holdsSomething = spans.Any(s => s.X >= x && s.Right <= right && s.Y >= y && s.Bottom <= bottom)
                || shapes.Any(g => g.Kind == PdfGraphicKind.Image && g.X >= x - Tolerance && g.Right <= right + Tolerance);
            if (!holdsSomething || area < 100)
            {
                continue;
            }

            // A frame around a chart is a picture, not a table: drawings (not cell colours) fill much of it, or it
            // is a single box with a picture or drawing in it.
            var framed = rows.Count == 2 && columns.Count == 2 && shapes.Any(g => (g.Fill is null || !CoversCell(g, columns, rows)) && g.X >= x - Tolerance && g.Right <= right + Tolerance && g.Y >= y - Tolerance && g.Bottom <= bottom + Tolerance);
            tables.Add(new FoundTable(columns, rows, rules, Open: false, Figure: drawn > area * 0.1 || framed));
        }

        return tables;
    }

    /// <summary>
    /// A column of text just right of a table, level with its rows (a spreadsheet's last column printed without
    /// borders), joins the table as a column without lines.
    /// </summary>
    private static FoundTable WithTextBeside(FoundTable table, List<PdfTextSpan> spans)
    {
        var (right, top, bottom) = (table.Columns[^1], table.Rows[0], table.Rows[^1]);
        var beside = spans.Where(s => s.X >= right - 1 && s.X < right + 150 && s.Y + (s.Height / 2) > top && s.Y + (s.Height / 2) < bottom).ToList();
        var rowsHit = beside.Select(s => table.Rows.FindLastIndex(y => y <= s.Y + (s.Height / 2))).Distinct().Count();
        if (rowsHit < 3 || rowsHit * 3 < table.Rows.Count - 1)
        {
            return table;
        }

        return table with { Columns = [.. table.Columns, beside.Max(s => s.Right) + 2], LooseFrom = table.Columns.Count - 1 };
    }

    /// <summary>True when a filled box covers most of a grid cell: it is the cell's colour.</summary>
    private static bool CoversCell(PdfGraphic fill, List<double> columns, List<double> rows)
    {
        for (var r = 0; r + 1 < rows.Count; r++)
        {
            for (var c = 0; c + 1 < columns.Count; c++)
            {
                var overlap = Math.Max(0, Math.Min(fill.Right, columns[c + 1]) - Math.Max(fill.X, columns[c]))
                    * Math.Max(0, Math.Min(fill.Bottom, rows[r + 1]) - Math.Max(fill.Y, rows[r]));
                if (overlap >= (columns[c + 1] - columns[c]) * (rows[r + 1] - rows[r]) * 0.7)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Tables drawn only with lines above and below (common in reports): long horizontal lines of the same
    /// width, stacked, with text between them in columns. Rows come from the text lines; the part above the
    /// second line is the heading row.
    /// </summary>
    /// <param name="pieces">The horizontal lines as drawn: tables often draw them one cell at a time, which shows the columns.</param>
    private static List<FoundTable> OpenTables(List<PdfRule> horizontal, List<PdfRule> pieces, List<PdfTextSpan> spans, List<FoundTable> taken)
    {
        var tables = new List<FoundTable>();
        var candidates = horizontal.Where(r => r.Length >= MinOpenTableRule)
            .Where(r => !taken.Any(t => r.Y1 >= t.Rows[0] - Tolerance && r.Y1 <= t.Rows[^1] + Tolerance && r.X2 > t.Columns[0] && r.X1 < t.Columns[^1]))
            .OrderBy(r => r.Y1)
            .ToList();
        while (candidates.Count > 0)
        {
            var first = candidates[0];
            var stack = new List<PdfRule> { first };
            foreach (var rule in candidates.Skip(1))
            {
                if (Math.Abs(rule.X1 - first.X1) <= 4 && Math.Abs(rule.X2 - first.X2) <= 4 && rule.Y1 - stack[^1].Y1 <= 200)
                {
                    stack.Add(rule);
                }
            }

            candidates.RemoveAll(stack.Contains);
            if (stack.Count < 2)
            {
                continue;
            }

            var (x, right, top, bottom) = (first.X1, first.X2, stack[0].Y1, stack[^1].Y1);
            var inside = spans.Where(s => s.X + (s.Width / 2) > x && s.X + (s.Width / 2) < right && s.Y + (s.Height / 2) > top && s.Y + (s.Height / 2) < bottom).ToList();
            var lines = TextLayout.Lines(new PdfPageText(0, 0, inside));
            var cellsPerLine = lines.Select(TextLayout.Cells).ToList();
            if (lines.Count == 0 || cellsPerLine.Count(c => c.Count > 1) * 2 < lines.Count)
            {
                continue; // Lines around ordinary paragraphs.
            }

            // Columns: bands where the cells sit, split halfway between neighbouring bands.
            var bands = new List<(double Left, double Right)>();
            foreach (var (_, l, r) in cellsPerLine.Where(c => c.Count > 1).SelectMany(c => c).OrderBy(c => c.Left))
            {
                if (bands.Count > 0 && l <= bands[^1].Right + Tolerance)
                {
                    bands[^1] = (bands[^1].Left, Math.Max(bands[^1].Right, r));
                }
                else
                {
                    bands.Add((l, r));
                }
            }

            var columns = new List<double> { x };
            var breaks = ColumnBreaks(stack, pieces);
            if (breaks.Count > 0)
            {
                columns.AddRange(breaks);
            }
            else
            {
                for (var i = 1; i < bands.Count; i++)
                {
                    columns.Add((bands[i - 1].Right + bands[i].Left) / 2);
                }
            }

            columns.Add(right);

            // Rows: the heading (above the second line when there are three or more), then one per text line.
            var rows = new List<double> { top };
            var bodyFrom = stack.Count >= 3 ? stack[1].Y1 : top;
            if (stack.Count >= 3)
            {
                rows.Add(bodyFrom);
            }

            var body = lines.Where(l => l.Min(s => s.Y) >= bodyFrom - Tolerance).ToList();
            for (var i = 1; i < body.Count; i++)
            {
                var split = (body[i - 1].Max(s => s.Bottom) + body[i].Min(s => s.Y)) / 2;
                var ruleBetween = stack.FirstOrDefault(s => s.Y1 > body[i - 1].Max(t => t.Bottom) && s.Y1 < body[i].Min(t => t.Y));
                rows.Add(ruleBetween?.Y1 ?? split);
            }

            rows.Add(bottom);
            tables.Add(new FoundTable(columns, Edges(rows), stack, Open: true));
        }

        return tables;
    }

    /// <summary>
    /// Cuts text that runs across a column line of the table into one piece per cell (an account number and its
    /// name are often one piece of text in PDFs made by Excel).
    /// </summary>
    private static void SplitAtColumns(List<PdfTextSpan> spans, FoundTable table)
    {
        var edges = table.Columns.Skip(1).SkipLast(1).ToList();
        for (var i = spans.Count - 1; i >= 0; i--)
        {
            var span = spans[i];
            var middle = span.Y + (span.Height / 2);
            if (span.CharLefts is not { } lefts || lefts.Count != span.Text.Length || middle < table.Rows[0] || middle > table.Rows[^1])
            {
                continue;
            }

            var cuts = new List<int>();
            // Only where the row has the line (a long name runs on over the next cell when that cell has none).
            var row = Math.Max(0, table.Rows.FindLastIndex(y => y <= middle));
            var (rowTop, rowBottom) = (table.Rows[Math.Min(row, table.Rows.Count - 2)], table.Rows[Math.Min(row + 1, table.Rows.Count - 1)]);
            foreach (var edge in edges.Where(e => e > span.X + 1 && e < span.Right - 1 && (table.Open || Covered(table.Rules, false, e, rowTop, rowBottom))))
            {
                // A word starting right at the line starts the next cell (an account number, then its name).
                // Otherwise the text runs on past its cell, as long names do in a spreadsheet: into an empty cell it
                // stays whole; where the next cell has its own text the overflow is hidden in the PDF and belongs to
                // this cell, so the cut moves on to the next space.
                var cut = Enumerable.Range(1, lefts.Count - 1).FirstOrDefault(k => lefts[k] >= edge - 0.5);
                if (cut <= 0)
                {
                    continue;
                }

                var startsCell = char.IsWhiteSpace(span.Text[cut - 1]) && lefts[cut] - edge < 6;

                // A word past the line set apart by a wide gap is the next cell's own (an amount after a long name).
                var apart = Enumerable.Range(cut, lefts.Count - cut).FirstOrDefault(k =>
                    k > 0 && char.IsWhiteSpace(span.Text[k - 1]) && !char.IsWhiteSpace(span.Text[k]) && lefts[k] - lefts[k - 1] >= span.FontSize * 0.8);
                if (!startsCell && apart > 0)
                {
                    cut = apart;
                }
                else if (!startsCell)
                {
                    var next = edges.FirstOrDefault(e => e > edge + 1, table.Columns[^1]);
                    var occupied = spans.Any(other => !ReferenceEquals(other, span)
                        && other.X + (other.Width / 2) > edge && other.X + (other.Width / 2) < next
                        && other.Y + (other.Height / 2) > rowTop && other.Y + (other.Height / 2) < rowBottom);
                    if (!occupied)
                    {
                        continue;
                    }

                    while (cut < lefts.Count && !char.IsWhiteSpace(span.Text[cut - 1]) && !char.IsWhiteSpace(span.Text[cut]))
                    {
                        cut++;
                    }
                }

                if (cut < lefts.Count && !cuts.Contains(cut))
                {
                    cuts.Add(cut);
                }
            }

            if (cuts.Count == 0)
            {
                continue;
            }

            var pieces = new List<PdfTextSpan>();
            var bounds = cuts.Order().Prepend(0).Append(lefts.Count).ToList();
            for (var p = 0; p + 1 < bounds.Count; p++)
            {
                var (from, to) = (bounds[p], bounds[p + 1]);
                var pieceLeft = p == 0 ? span.X : lefts[from];
                var pieceRight = to == lefts.Count ? span.Right : lefts[to];
                var piece = span with { Text = span.Text[from..to], X = pieceLeft, Width = Math.Max(0.1, pieceRight - pieceLeft), CharLefts = lefts.Skip(from).Take(to - from).ToList() };
                if (piece.Text.Trim().Length > 0)
                {
                    pieces.Add(piece);
                }
            }

            spans.RemoveAt(i);
            spans.InsertRange(i, pieces);
        }
    }

    /// <summary>Where the stacked lines are broken into pieces at the same places: the column edges, left to right.</summary>
    private static List<double> ColumnBreaks(List<PdfRule> stack, List<PdfRule> pieces)
    {
        var ends = new List<double>();
        foreach (var rule in stack)
        {
            ends.AddRange(pieces
                .Where(p => Math.Abs(p.Y1 - rule.Y1) <= 1 && p.X1 >= rule.X1 - Tolerance && p.X2 <= rule.X2 + Tolerance && p.X2 < rule.X2 - 4)
                .Select(p => p.X2)
                .Distinct());
        }

        var needed = Math.Max(2, stack.Count - 1);
        return Edges(ends).Where(edge => ends.Count(e => Math.Abs(e - edge) <= Tolerance * 1.5) >= needed).ToList();
    }

    private static LayoutTable BuildTable(FoundTable table, List<PdfTextSpan> spans, List<PdfGraphic> shapes, PdfPageText page)
    {
        var (columns, rows) = (table.Columns, table.Rows);
        int rowCount = rows.Count - 1, columnCount = columns.Count - 1;

        // Merged cells: neighbouring grid cells with no line between them (only for tables drawn on all sides).
        var group = new int[rowCount, columnCount];
        for (var r = 0; r < rowCount; r++)
        {
            for (var c = 0; c < columnCount; c++)
            {
                group[r, c] = (r * columnCount) + c;
            }
        }

        if (!table.Open)
        {
            var parent = Enumerable.Range(0, rowCount * columnCount).ToArray();
            int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
            for (var r = 0; r < rowCount; r++)
            {
                for (var c = 0; c < columnCount; c++)
                {
                    if (c + 1 < columnCount && c + 1 < table.LooseFrom && !Covered(table.Rules, false, columns[c + 1], rows[r], rows[r + 1]))
                    {
                        parent[Find((r * columnCount) + c)] = Find((r * columnCount) + c + 1);
                    }

                    if (r + 1 < rowCount && c < table.LooseFrom && !Covered(table.Rules, true, rows[r + 1], columns[c], columns[c + 1]))
                    {
                        parent[Find((r * columnCount) + c)] = Find(((r + 1) * columnCount) + c);
                    }
                }
            }

            // Only rectangles become merged cells; any other shape stays separate cells.
            foreach (var members in Enumerable.Range(0, rowCount * columnCount).GroupBy(Find))
            {
                var cells = members.Select(i => (Row: i / columnCount, Column: i % columnCount)).ToList();
                int r0 = cells.Min(m => m.Row), r1 = cells.Max(m => m.Row), c0 = cells.Min(m => m.Column), c1 = cells.Max(m => m.Column);
                var rectangle = cells.Count == (r1 - r0 + 1) * (c1 - c0 + 1);
                foreach (var (row, column) in cells)
                {
                    group[row, column] = rectangle ? (r0 * columnCount) + c0 : (row * columnCount) + column;
                }
            }
        }

        var built = new List<LayoutCell>();
        foreach (var anchor in Enumerable.Range(0, rowCount * columnCount).Where(i => group[i / columnCount, i % columnCount] == i))
        {
            int r0 = anchor / columnCount, c0 = anchor % columnCount, r1 = r0, c1 = c0;
            while (r1 + 1 < rowCount && group[r1 + 1, c0] == anchor)
            {
                r1++;
            }

            while (c1 + 1 < columnCount && group[r0, c1 + 1] == anchor)
            {
                c1++;
            }

            double left = columns[c0], right = columns[c1 + 1], top = rows[r0], bottom = rows[r1 + 1];
            bool In(double x, double y) => x >= left && x < right && y >= top && y < bottom;
            var cellSpans = spans.Where(s => In(s.X + (s.Width / 2), s.Y + (s.Height / 2))).ToList();
            var cellShapes = shapes.Where(g => In(g.X + (g.Width / 2), g.Y + (g.Height / 2))).ToList();

            // The cell's colour: filled boxes of one colour covering most of it (often drawn as several boxes).
            var fills = shapes.Where(g => g.Fill is not null && g.Right > left && g.X < right && g.Bottom > top && g.Y < bottom).ToList();
            var fill = fills.Select(g => g.Fill!.Value).Distinct()
                .Select(color => (Color: color, Share: Coverage(fills.Where(g => g.Fill == color), left, top, right, bottom)))
                .Where(c => c.Share >= 0.6)
                .OrderByDescending(c => c.Share)
                .Select(c => (PdfColor?)c.Color)
                .FirstOrDefault();
            cellShapes.RemoveAll(g => g.Fill is not null);

            var borders = CellBorders.None;
            if (Covered(table.Rules, true, top, left, right))
            {
                borders |= CellBorders.Top;
            }

            if (Covered(table.Rules, true, bottom, left, right))
            {
                borders |= CellBorders.Bottom;
            }

            if (Covered(table.Rules, false, left, top, bottom))
            {
                borders |= CellBorders.Left;
            }

            if (Covered(table.Rules, false, right, top, bottom))
            {
                borders |= CellBorders.Right;
            }

            var content = Flow(cellSpans, cellShapes, [], page, left + CellPadding, right - CellPadding, left + CellPadding);
            built.Add(new LayoutCell(r0, c0, r1 - r0 + 1, c1 - c0 + 1, content, borders, fill));
        }

        RunOverEmptyCells(built, columns);
        SpanLoneValues(built, rows);
        return new LayoutTable(
            columns[0],
            rows[0],
            Enumerable.Range(0, columnCount).Select(c => columns[c + 1] - columns[c]).ToList(),
            Enumerable.Range(0, rowCount).Select(r => rows[r + 1] - rows[r]).ToList(),
            built);
    }

    /// <summary>
    /// A value set in the middle of several rows with no line between them ("55.93" beside the two rows of the
    /// column next to it) becomes one cell over those rows, as it looks on the page. As a cell of its own thin row
    /// it would not fit its text in Word, which makes the row, and the page, taller.
    /// </summary>
    private static void SpanLoneValues(List<LayoutCell> cells, IReadOnlyList<double> rows)
    {
        LayoutCell? At(int row, int column) =>
            cells.FirstOrDefault(c => c.Row == row && c.Column == column && c is { RowSpan: 1, ColumnSpan: 1 });

        foreach (var cell in cells.Where(c => c is { RowSpan: 1, ColumnSpan: 1, Content.Count: > 0 }).ToList())
        {
            int first = cell.Row, last = cell.Row;
            while (At(first - 1, cell.Column) is { Content.Count: 0 } above && !above.Borders.HasFlag(CellBorders.Bottom)
                && !At(first, cell.Column)!.Borders.HasFlag(CellBorders.Top))
            {
                first--;
            }

            while (At(last + 1, cell.Column) is { Content.Count: 0 } below && !below.Borders.HasFlag(CellBorders.Top)
                && !At(last, cell.Column)!.Borders.HasFlag(CellBorders.Bottom))
            {
                last++;
            }

            // Only where the value sits in the middle of those rows, as a value set over them does.
            var middle = (cell.Content.Min(b => b.Top) + cell.Content.Max(b => b.Bottom)) / 2;
            if (first == cell.Row || last == cell.Row
                || Math.Abs(middle - ((rows[first] + rows[last + 1]) / 2)) > (rows[last + 1] - rows[first]) * 0.2)
            {
                continue;
            }

            var borders = (At(first, cell.Column)!.Borders & CellBorders.Top) | (At(last, cell.Column)!.Borders & CellBorders.Bottom)
                | (cell.Borders & (CellBorders.Left | CellBorders.Right));
            cells.RemoveAll(c => c.Column == cell.Column && c.Row >= first && c.Row <= last && c is { RowSpan: 1, ColumnSpan: 1 });
            cells.Add(cell with { Row = first, RowSpan = last - first + 1, Borders = borders });
        }
    }

    /// <summary>The share of a box covered by any of <paramref name="boxes"/>, measured on a grid of points.</summary>
    private static double Coverage(IEnumerable<PdfGraphic> boxes, double left, double top, double right, double bottom)
    {
        const int Steps = 8;
        var list = boxes.ToList();
        var hits = 0;
        for (var i = 0; i < Steps; i++)
        {
            for (var j = 0; j < Steps; j++)
            {
                var x = left + ((right - left) * (i + 0.5) / Steps);
                var y = top + ((bottom - top) * (j + 0.5) / Steps);
                if (list.Any(g => x >= g.X && x <= g.Right && y >= g.Y && y <= g.Bottom))
                {
                    hits++;
                }
            }
        }

        return hits / (double)(Steps * Steps);
    }

    /// <summary>
    /// A line of text longer than its cell takes the empty cells beside it, as a spreadsheet shows it (Word
    /// would otherwise wrap it and make the row taller).
    /// </summary>
    private static void RunOverEmptyCells(List<LayoutCell> cells, List<double> columns)
    {
        foreach (var cell in cells.Where(c => c.RowSpan == 1).OrderBy(c => c.Row).ThenBy(c => c.Column).ToList())
        {
            if (!cells.Contains(cell) || cell.Content.OfType<LayoutParagraph>().ToList() is not [{ Paragraph: { LineCount: 1 } line }])
            {
                continue;
            }

            var span = cell.ColumnSpan;
            var taken = new List<LayoutCell>();
            while (line.Right > columns[cell.Column + span] + 1 && cell.Column + span < columns.Count - 1
                && cells.FirstOrDefault(c => c.Row == cell.Row && c.Column == cell.Column + span) is { RowSpan: 1, Content.Count: 0 } next)
            {
                taken.Add(next);
                span += next.ColumnSpan;
            }

            if (taken.Count > 0)
            {
                cells.RemoveAll(taken.Contains);
                var right = taken[^1].Borders & CellBorders.Right;
                cells[cells.IndexOf(cell)] = cell with { ColumnSpan = span, Borders = (cell.Borders & ~CellBorders.Right) | right };
            }
        }
    }

    // --- Pictures and paragraphs ------------------------------------------------------------------

    /// <summary>Paragraphs and pictures inside an area (the page outside tables, or one cell), top to bottom.</summary>
    private static List<LayoutBlock> Flow(
        List<PdfTextSpan> spans, List<PdfGraphic> shapes, List<PdfRule> rules, PdfPageText page, double areaLeft, double areaRight, double? indentFrom)
    {
        var pictures = Pictures(shapes, rules, spans, page);
        bool OnPicture(PdfTextSpan s) => pictures.Any(p =>
            s.X + (s.Width / 2) >= p.X && s.X + (s.Width / 2) <= p.Right && s.Y + (s.Height / 2) >= p.Y && s.Y + (s.Height / 2) <= p.Bottom);

        var text = spans.Where(s => !OnPicture(s)).ToList();
        var blocks = new List<LayoutBlock>(pictures);
        if (text.Count > 0)
        {
            var paragraphs = TextLayout.Paragraphs(new PdfPageText(page.Width, page.Height, text), areaLeft, areaRight, indentFrom);
            blocks.AddRange(paragraphs.Select(p => new LayoutParagraph(p)));
        }

        return [.. blocks.OrderBy(b => b.Top).ThenBy(b => b.Left)];
    }

    /// <summary>
    /// Pictures and drawings grouped into the regions to copy. A filled box under text is a background, and a
    /// picture behind most of the page's text is a letterhead or watermark: both are left out.
    /// </summary>
    private static List<LayoutPicture> Pictures(List<PdfGraphic> shapes, List<PdfRule> rules, List<PdfTextSpan> spans, PdfPageText page)
    {
        int TextOn(double x, double y, double right, double bottom) =>
            spans.Count(s => s.X + (s.Width / 2) >= x && s.X + (s.Width / 2) <= right && s.Y + (s.Height / 2) >= y && s.Y + (s.Height / 2) <= bottom);

        var regions = new List<(double X, double Y, double Right, double Bottom, bool Photo)>();
        foreach (var g in shapes)
        {
            if (g.Fill is not null && TextOn(g.X, g.Y, g.Right, g.Bottom) > 0)
            {
                continue; // A coloured box behind text.
            }

            if (g.Width * g.Height > page.Width * page.Height * 0.5 && TextOn(g.X, g.Y, g.Right, g.Bottom) > 10)
            {
                continue; // Background or watermark.
            }

            regions.Add((g.X, g.Y, g.Right, g.Bottom, g.Kind == PdfGraphicKind.Image));
        }

        // Join regions that touch or overlap (a chart's bars, labels' boxes, axes), then add touching lines.
        bool Touch((double X, double Y, double Right, double Bottom, bool Photo) a, double x, double y, double right, double bottom) =>
            a.X <= right + 3 && x <= a.Right + 3 && a.Y <= bottom + 3 && y <= a.Bottom + 3;

        for (var changed = true; changed;)
        {
            changed = false;
            for (var i = 0; i < regions.Count && !changed; i++)
            {
                for (var j = i + 1; j < regions.Count && !changed; j++)
                {
                    var (a, b) = (regions[i], regions[j]);

                    // Photos side by side stay separate pictures (overlapping ones are pieces of one); drawings merge.
                    if (a.Photo && b.Photo && (Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X) <= 1 || Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y) <= 1))
                    {
                        continue;
                    }

                    if (Touch(a, b.X, b.Y, b.Right, b.Bottom))
                    {
                        regions[i] = (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom), a.Photo || b.Photo);
                        regions.RemoveAt(j);
                        changed = true;
                    }
                }
            }

            for (var i = 0; i < regions.Count && !changed; i++)
            {
                if (regions[i].Photo)
                {
                    continue;
                }

                foreach (var rule in rules)
                {
                    var r = regions[i];
                    if (Touch(r, rule.X1, rule.Y1, rule.X2, rule.Y2) && (rule.X1 < r.X || rule.X2 > r.Right || rule.Y1 < r.Y || rule.Y2 > r.Bottom))
                    {
                        regions[i] = (Math.Min(r.X, rule.X1), Math.Min(r.Y, rule.Y1), Math.Max(r.Right, rule.X2), Math.Max(r.Bottom, rule.Y2), false);
                        changed = true;
                    }
                }
            }
        }

        return regions
            .Where(r => r.Right - r.X >= 4 && r.Bottom - r.Y >= 4)
            .Select(r => new LayoutPicture(r.X, r.Y, r.Right - r.X, r.Bottom - r.Y, r.Photo))
            .ToList();
    }
}
