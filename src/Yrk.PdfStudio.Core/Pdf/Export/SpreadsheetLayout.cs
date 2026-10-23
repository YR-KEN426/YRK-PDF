namespace Yrk.PdfStudio.Core.Pdf.Export;

/// <summary>
/// Turns a page into worksheet rows: tables drawn with lines keep their grid, merged cells, borders and colours;
/// other text is split into columns where it has wide gaps (as <see cref="TextLayout.Table"/> does).
/// Pictures are left out.
/// </summary>
public static class SpreadsheetLayout
{
    public static XlsxSheet Sheet(string name, PdfPageText text, IReadOnlyList<LayoutBlock> blocks)
    {
        var tables = blocks.OfType<LayoutTable>().ToList();
        var pictures = blocks.OfType<LayoutPicture>().ToList();
        bool InArea(PdfTextSpan s)
        {
            double x = s.X + (s.Width / 2), y = s.Y + (s.Height / 2);
            return tables.Any(t => x >= t.X && x <= t.X + t.Width && y >= t.Top && y <= t.Bottom)
                || pictures.Any(p => x >= p.X && x <= p.Right && y >= p.Y && y <= p.Bottom);
        }

        // Text outside tables: one row per line, columns found across all those lines.
        var free = new PdfPageText(text.Width, text.Height, text.Spans.Where(s => !InArea(s)).ToList());
        var lines = TextLayout.Lines(free);
        var lineTops = lines.Select(l => l.Min(s => s.Y)).ToList();
        var freeTable = TextLayout.Table(free);

        // With a table on the page, the text around it (titles, column group headings) goes in the table's columns.
        if (tables.MaxBy(t => t.Width) is { } main)
        {
            var edges = main.ColumnWidths.Select((_, c) => main.X + main.ColumnWidths.Take(c).Sum()).ToList();
            var aligned = lines.Select(line =>
            {
                var row = new string?[edges.Count];
                foreach (var (cellText, cellLeft, _) in TextLayout.Cells(line))
                {
                    var column = Math.Max(0, edges.FindLastIndex(e => e <= cellLeft + 2));
                    row[column] = row[column] is { } before ? before + " " + cellText : cellText;
                }

                return (IReadOnlyList<string?>)row;
            }).ToList();
            freeTable = new TextTable(main.ColumnWidths, aligned);
        }

        var items = lineTops.Select((top, i) => (Top: top, Row: (IReadOnlyList<string?>?)freeTable.Rows[i], Table: (LayoutTable?)null))
            .Concat(tables.Select(t => (Top: t.Top, Row: (IReadOnlyList<string?>?)null, Table: (LayoutTable?)t)))
            .OrderBy(item => item.Top)
            .ToList();

        var rows = new List<IReadOnlyList<string?>>();
        var styles = new Dictionary<(int Row, int Column), XlsxStyle>();
        var merges = new List<XlsxMerge>();
        var heights = new Dictionary<int, double>();
        var widths = freeTable.Rows.Count > 0 ? freeTable.ColumnWidths.ToList() : [];
        foreach (var (_, row, table) in items)
        {
            if (row is not null)
            {
                rows.Add(row);
                continue;
            }

            var first = rows.Count;
            for (var c = 0; c < table!.ColumnWidths.Count; c++)
            {
                if (c < widths.Count)
                {
                    widths[c] = Math.Max(widths[c], table.ColumnWidths[c]);
                }
                else
                {
                    widths.Add(table.ColumnWidths[c]);
                }
            }

            for (var r = 0; r < table.RowHeights.Count; r++)
            {
                rows.Add(new string?[table.ColumnWidths.Count]);
                if (table.Cells.Any(c => c.Row == r && c.Content.OfType<LayoutParagraph>().Sum(p => p.Paragraph.LineCount) > 1))
                {
                    heights[first + r] = table.RowHeights[r]; // Several lines: as tall as on the page. Others keep Excel's height.
                }
            }

            foreach (var cell in table.Cells)
            {
                var paragraphs = cell.Content.OfType<LayoutParagraph>().Select(p => p.Paragraph).ToList();
                var value = string.Join("\n", paragraphs.Select(p => string.Concat(p.Runs.Select(r => r.IsTab ? " " : r.Text)).Trim()));
                ((string?[])rows[first + cell.Row])[cell.Column] = value.Length > 0 ? value : null;
                var look = new XlsxStyle(
                    cell.Borders,
                    cell.Fill,
                    Bold: paragraphs.Count > 0 && paragraphs.All(p => p.Runs.Where(r => !r.IsTab).All(r => r.Bold)),
                    Wrap: value.Contains('\n'));

                // Every grid cell of a merged cell carries its look, so the borders are drawn all round.
                for (var r = cell.Row; r < cell.Row + cell.RowSpan; r++)
                {
                    for (var c = cell.Column; c < cell.Column + cell.ColumnSpan; c++)
                    {
                        styles[(first + r, c)] = look;
                    }
                }

                if (cell.RowSpan > 1 || cell.ColumnSpan > 1)
                {
                    merges.Add(new XlsxMerge(first + cell.Row, cell.Column, cell.RowSpan, cell.ColumnSpan));
                }
            }
        }

        return new XlsxSheet(name, widths, rows, styles, merges, heights);
    }

    /// <summary>Several sheets on one, one after the other with an empty row between them.</summary>
    public static XlsxSheet Combine(string name, IReadOnlyList<XlsxSheet> sheets)
    {
        var rows = new List<IReadOnlyList<string?>>();
        var styles = new Dictionary<(int Row, int Column), XlsxStyle>();
        var merges = new List<XlsxMerge>();
        var heights = new Dictionary<int, double>();
        var columns = sheets.Count == 0 ? 0 : sheets.Max(s => s.ColumnWidths.Count);
        var widths = Enumerable.Range(0, columns).Select(c => sheets.Max(s => c < s.ColumnWidths.Count ? s.ColumnWidths[c] : 0)).ToList();
        foreach (var sheet in sheets)
        {
            if (rows.Count > 0)
            {
                rows.Add([]);
            }

            var offset = rows.Count;
            rows.AddRange(sheet.Rows);
            foreach (var (key, look) in sheet.Styles ?? new Dictionary<(int Row, int Column), XlsxStyle>())
            {
                styles[(key.Row + offset, key.Column)] = look;
            }

            merges.AddRange((sheet.Merges ?? []).Select(m => m with { Row = m.Row + offset }));
            foreach (var (row, height) in sheet.RowHeights ?? new Dictionary<int, double>())
            {
                heights[row + offset] = height;
            }
        }

        return new XlsxSheet(name, widths, rows, styles, merges, heights);
    }
}
