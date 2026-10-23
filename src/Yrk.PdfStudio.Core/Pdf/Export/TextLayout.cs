using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Export;

/// <summary>
/// A run of text in one style; "\t" alone stands for a tab (a wide gap on the line), with <see cref="TabTo"/> the
/// page position (points) where the text after it starts. <see cref="StartsLine"/> marks the first run of a later
/// line of its paragraph on the PDF.
/// </summary>
public sealed record TextRun(string Text, double FontSize, string FontName, bool Bold, bool Italic, PdfColor Color, double TabTo = 0, bool StartsLine = false)
{
    public bool IsTab => Text == "\t";
}

/// <summary>A paragraph rebuilt from page lines. Distances are in points; Top, Bottom and Left are where it is on the page.</summary>
public sealed record TextParagraph(
    IReadOnlyList<TextRun> Runs, double Indent, double SpaceBefore, bool Centered, double Top = 0, double Bottom = 0, double Left = 0, bool RightAligned = false,
    int LineCount = 1, double LinePitch = 0, double Right = 0)
{
    /// <summary>The largest text size in the paragraph.</summary>
    public double FontSize => Runs.Count == 0 ? 0 : Runs.Max(r => r.FontSize);
}

/// <summary>Text laid out as a grid: one string (or null) per column, and each column's width in points.</summary>
public sealed record TextTable(IReadOnlyList<double> ColumnWidths, IReadOnlyList<IReadOnlyList<string?>> Rows);

/// <summary>
/// Rebuilds lines, paragraphs and table columns from positioned text (PDFs store text as placed pieces,
/// not as paragraphs). The rules are simple and predictable: good for letters, reports and plain tables;
/// complex layouts (several text columns, text in shapes) come out as lines in reading order.
/// </summary>
public static class TextLayout
{
    /// <summary>Spans on the same line, top to bottom, each line left to right.</summary>
    public static IReadOnlyList<IReadOnlyList<PdfTextSpan>> Lines(PdfPageText page)
    {
        var rows = new List<List<PdfTextSpan>>();
        var bands = new List<(double Top, double Bottom)>();
        foreach (var span in page.Spans.OrderBy(s => s.Y + (s.Height / 2)).ThenBy(s => s.X))
        {
            var index = -1;
            for (var i = bands.Count - 1; i >= 0 && index < 0; i--)
            {
                var (top, bottom) = bands[i];
                var overlap = Math.Min(bottom, span.Bottom) - Math.Max(top, span.Y);
                if (overlap >= 0.5 * Math.Min(bottom - top, span.Height))
                {
                    index = i;
                }
            }

            if (index < 0)
            {
                rows.Add([span]);
                bands.Add((span.Y, span.Bottom));
            }
            else
            {
                rows[index].Add(span);
                bands[index] = (Math.Min(bands[index].Top, span.Y), Math.Max(bands[index].Bottom, span.Bottom));
            }
        }

        AttachRaisedMarks(rows);
        return rows
            .Select(row => (IReadOnlyList<PdfTextSpan>)row.OrderBy(s => s.X).ToList())
            .OrderBy(row => row.Min(s => s.Y))
            .ToList();
    }

    /// <summary>
    /// A small raised or lowered mark ("®", "*", "2" of m²) barely overlaps its line, so it forms a line of its own,
    /// which becomes an extra paragraph in Word and pushes the page over. A row of at most three such characters,
    /// smaller than a line it touches and right after (or within) that line's text, joins that line.
    /// </summary>
    private static void AttachRaisedMarks(List<List<PdfTextSpan>> rows)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var row = rows[i];
            if (row.Sum(s => s.Text.Trim().Length) is 0 or > 3)
            {
                continue;
            }

            var top = row.Min(s => s.Y);
            var bottom = row.Max(s => s.Bottom);
            var left = row.Min(s => s.X);
            var size = row.Max(s => s.FontSize);
            var host = rows.FirstOrDefault(other => other != row
                && other.Max(s => s.FontSize) * 0.85 >= size
                && bottom >= other.Min(s => s.Y) - (size * 0.5)
                && top <= other.Max(s => s.Bottom)
                && other.Any(s => left >= s.X - size && left <= s.X + s.Width + (size * 1.5)));
            if (host is not null)
            {
                host.AddRange(row);
                rows.RemoveAt(i);
            }
        }
    }

    /// <summary>The runs of one line, with a space or a tab where the gap between pieces asks for one.</summary>
    public static IReadOnlyList<TextRun> LineRuns(IReadOnlyList<PdfTextSpan> line)
    {
        var runs = new List<TextRun>();
        PdfTextSpan? previous = null;
        foreach (var span in line)
        {
            var text = span.Text;
            if (previous is not null)
            {
                var gap = span.X - previous.Right;
                var size = Math.Max(previous.FontSize, span.FontSize);
                if (gap > TabGap(size))
                {
                    runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd() };
                    runs.Add(Run("\t", previous) with { TabTo = span.X });
                    text = text.TrimStart();
                }
                else if (gap > size * 0.2 && !char.IsWhiteSpace(runs[^1].Text[^1]) && !char.IsWhiteSpace(text[0]) && !FollowsLetter(text[0]))
                {
                    text = " " + text;
                }
            }

            runs.Add(Run(text, span));
            previous = span;
        }

        if (runs.Count > 0)
        {
            runs[0] = runs[0] with { Text = runs[0].Text.TrimStart() };
            runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd() };
        }

        return runs.Where(r => r.Text.Length > 0).ToList();
    }

    /// <summary>Joins lines into paragraphs: lines close together, of the same size, and filling the width.</summary>
    public static IReadOnlyList<TextParagraph> Paragraphs(PdfPageText page) => Paragraphs(page, 0, page.Width, null);

    /// <summary>
    /// Paragraphs of text inside an area (a page, or a table cell from <paramref name="areaLeft"/> to <paramref name="areaRight"/>).
    /// Indents are measured from <paramref name="indentFrom"/>, or from the leftmost line when null.
    /// </summary>
    public static IReadOnlyList<TextParagraph> Paragraphs(PdfPageText page, double areaLeft, double areaRight, double? indentFrom)
    {
        var lines = Lines(page);
        var paragraphs = new List<TextParagraph>();
        if (lines.Count == 0)
        {
            return paragraphs;
        }

        var blockLeft = indentFrom ?? lines.Min(l => l[0].X);
        var inCell = indentFrom is not null;
        // The text width comes from ordinary lines: a table can reach further than the paragraphs.
        var prose = lines.Where(l => !HasTab(l)).ToList();
        var blockRight = (prose.Count > 0 ? prose : lines).Max(l => l[^1].Right);
        List<TextRun>? runs = null;
        IReadOnlyList<PdfTextSpan>? first = null, last = null;
        var spaceBefore = 0.0;
        var lineTops = new List<double>();
        double previousBottom = lines[0].Min(s => s.Y);

        void Close()
        {
            if (runs is { Count: > 0 } && first is not null && last is not null)
            {
                var left = first[0].X;
                var right = last[^1].Right;
                var size = first.Max(s => s.FontSize);
                var areaWidth = areaRight - areaLeft;
                var centered = first == last
                    && Math.Abs(((left + right) / 2) - ((areaLeft + areaRight) / 2)) < areaWidth * (inCell ? 0.08 : 0.03)
                    && left > blockLeft + (inCell ? 3 : size * 3);
                var rightAligned = inCell && !centered && first == last && areaRight - right < 4 && left - blockLeft > 8;
                var top = first.Min(s => s.Y);
                paragraphs.Add(new TextParagraph(
                    runs, centered || rightAligned ? 0 : Math.Max(0, left - blockLeft), spaceBefore, centered, top, last.Max(s => s.Bottom), left, rightAligned,
                    lineTops.Count, lineTops.Count > 1 ? (lineTops[^1] - lineTops[0]) / (lineTops.Count - 1) : 0, right));
            }
        }

        foreach (var line in lines)
        {
            var top = line.Min(s => s.Y);
            var lineRuns = LineRuns(line);
            if (lineRuns.Count == 0)
            {
                continue;
            }

            var startsNew = runs is null || last is null || StartsParagraph(last, line, blockRight);
            if (startsNew)
            {
                Close();
                spaceBefore = paragraphs.Count == 0 && runs is null ? 0 : Math.Clamp(top - previousBottom, 0, 72);
                runs = [.. lineRuns];
                first = line;
                lineTops = [top];
            }
            else
            {
                // Same paragraph: join with a space, except between Thai words (Thai has no spaces between words).
                var end = runs![^1].Text;
                var start = lineRuns[0].Text;
                if (end.EndsWith('-') && end.Length > 1 && char.IsLetter(end[^2]) && char.IsLower(start[0]))
                {
                    runs[^1] = runs[^1] with { Text = end[..^1] };
                }
                else if (!(IsThai(end[^1]) && IsThai(start[0])))
                {
                    runs[^1] = runs[^1] with { Text = end + " " };
                }

                runs.Add(lineRuns[0] with { StartsLine = true });
                runs.AddRange(lineRuns.Skip(1));
                lineTops.Add(top);
            }

            last = line;
            previousBottom = line.Max(s => s.Bottom);
        }

        Close();
        return paragraphs;
    }

    /// <summary>Plain text: lines as on the page (tabs for wide gaps), a blank line between paragraphs.</summary>
    public static string PlainText(PdfPageText page)
    {
        var text = new StringBuilder();
        IReadOnlyList<PdfTextSpan>? previous = null;
        var lines = Lines(page);
        foreach (var line in lines)
        {
            if (previous is not null)
            {
                text.AppendLine();
                if (line.Min(s => s.Y) - previous.Max(s => s.Bottom) > previous.Max(s => s.Height) * 0.7)
                {
                    text.AppendLine();
                }
            }

            text.Append(string.Concat(LineRuns(line).Select(r => r.Text)));
            previous = line;
        }

        return text.ToString();
    }

    /// <summary>
    /// Lines as table rows: pieces with a wide gap between them become cells, and columns are found from
    /// where cells sit across all lines (so right-aligned numbers still land in their column).
    /// </summary>
    public static TextTable Table(PdfPageText page)
    {
        var lines = Lines(page).Select(Cells).ToList();

        // Column bands: the merged horizontal extents of cells on lines with two or more cells.
        var extents = lines.Where(cells => cells.Count > 1)
            .SelectMany(cells => cells)
            .Select(c => (c.Left, c.Right))
            .OrderBy(e => e.Left)
            .ToList();
        var bands = new List<(double Left, double Right)>();
        foreach (var (left, right) in extents)
        {
            if (bands.Count > 0 && left <= bands[^1].Right + 2)
            {
                bands[^1] = (bands[^1].Left, Math.Max(bands[^1].Right, right));
            }
            else
            {
                bands.Add((left, right));
            }
        }

        if (bands.Count == 0)
        {
            var all = lines.SelectMany(c => c).ToList();
            bands.Add(all.Count == 0 ? (0, page.Width) : (all.Min(c => c.Left), all.Max(c => c.Right)));
        }

        var rows = new List<IReadOnlyList<string?>>();
        foreach (var cells in lines)
        {
            var row = new string?[bands.Count];
            foreach (var cell in cells)
            {
                var column = BestBand(bands, cell.Left, cell.Right);
                row[column] = row[column] is { } existing ? existing + " " + cell.Text : cell.Text;
            }

            rows.Add(row);
        }

        return new TextTable(bands.Select(b => b.Right - b.Left).ToList(), rows);
    }

    internal static List<(string Text, double Left, double Right)> Cells(IReadOnlyList<PdfTextSpan> line)
    {
        var cells = new List<(string Text, double Left, double Right)>();
        var text = new StringBuilder();
        double left = 0, right = 0;
        PdfTextSpan? previous = null;
        foreach (var span in line)
        {
            if (previous is not null && span.X - previous.Right > TabGap(Math.Max(previous.FontSize, span.FontSize)))
            {
                cells.Add((text.ToString().Trim(), left, right));
                text.Clear();
                previous = null;
            }

            if (previous is null)
            {
                left = span.X;
            }
            else if (span.X - previous.Right > Math.Max(previous.FontSize, span.FontSize) * 0.2
                     && text.Length > 0 && !char.IsWhiteSpace(text[^1]) && !char.IsWhiteSpace(span.Text[0]))
            {
                text.Append(' ');
            }

            text.Append(span.Text);
            right = span.Right;
            previous = span;
        }

        if (text.Length > 0)
        {
            cells.Add((text.ToString().Trim(), left, right));
        }

        return cells.Where(c => c.Text.Length > 0).ToList();
    }

    private static int BestBand(List<(double Left, double Right)> bands, double left, double right)
    {
        var best = 0;
        var bestScore = double.MinValue;
        for (var i = 0; i < bands.Count; i++)
        {
            var overlap = Math.Min(bands[i].Right, right) - Math.Max(bands[i].Left, left);
            var score = overlap > 0 ? overlap : -Math.Abs(((bands[i].Left + bands[i].Right) / 2) - ((left + right) / 2)) - 10_000;
            if (score > bestScore)
            {
                (best, bestScore) = (i, score);
            }
        }

        return best;
    }

    private static bool StartsParagraph(IReadOnlyList<PdfTextSpan> previous, IReadOnlyList<PdfTextSpan> line, double blockRight)
    {
        var previousSize = previous.Max(s => s.FontSize);
        var size = line.Max(s => s.FontSize);
        var gap = line.Min(s => s.Y) - previous.Max(s => s.Bottom);
        var lineHeight = previous.Max(s => s.Height);
        return gap > lineHeight * 0.7                                  // A blank line between them.
            || Math.Abs(size - previousSize) > 1                       // A heading and its text.
            || previous[^1].Right < blockRight - (previousSize * 4)    // The previous line ended early.
            || line[0].X > previous[0].X + (size * 1.5)                // First-line indent.
            || line[0].X < previous[0].X - (size * 1.5)                // Back out (after a list item or quote).
            || HasTab(previous) || HasTab(line);                       // Table-like lines stay lines.
    }

    private static bool HasTab(IReadOnlyList<PdfTextSpan> line)
    {
        for (var i = 1; i < line.Count; i++)
        {
            if (line[i].X - line[i - 1].Right > TabGap(Math.Max(line[i].FontSize, line[i - 1].FontSize)))
            {
                return true;
            }
        }

        return false;
    }

    private static double TabGap(double fontSize) => fontSize * 1.5;

    private static TextRun Run(string text, PdfTextSpan style) =>
        new(text, style.FontSize, style.FontName, style.Bold, style.Italic, style.Color);

    private static bool IsThai(char c) => c is >= '฀' and <= '๿';

    /// <summary>Thai vowels and marks written after (or above or below) a letter: no space ever comes before them.</summary>
    public static bool FollowsLetter(char c) => c is (>= 'ะ' and <= 'ฺ') or 'ๅ' or (>= '็' and <= '๎');
}
