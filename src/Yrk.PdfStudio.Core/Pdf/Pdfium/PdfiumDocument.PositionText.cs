using System.Text;
using Yrk.PdfStudio.Core.Pdf.Export;

namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>
/// Copying and searching by position, for pages whose stored text does not read as printed. Excel (printing
/// through Windows) stores Thai out of order and PDFium breaks its lines inside words ("เจ\nา้หนี้"), so even the
/// repaired text (<see cref="RepairedText"/>) is wrong there. Exporting reads such pages right from where each
/// character is (<see cref="ReadTextSpans"/>); these pages are copied and searched from the same lines.
/// </summary>
internal sealed partial class PdfiumDocument
{
    /// <summary>A character as exported, with its box in points on the page as displayed.</summary>
    private readonly record struct PlacedChar(char C, double Left, double Right);

    /// <summary>A line of the page as exported: its characters, with a space between pieces, and its height.</summary>
    private sealed record PlacedLine(List<PlacedChar> Chars, double Top, double Bottom)
    {
        public string Text => string.Concat(Chars.Select(c => c.C));
    }

    private (int PageIndex, int Revision, List<PlacedLine>? Lines)? _placedTextCache;

    /// <summary>
    /// The page's lines read by position when its stored text reads wrong: when at least a fifth of the Thai pieces
    /// that exporting finds (and two or more) are nowhere in the repaired text. Null otherwise: PDFium's own
    /// character order is kept, with its exact selection.
    /// </summary>
    private List<PlacedLine>? PlacedLines(int pageIndex)
    {
        if (_placedTextCache is { } cached && cached.PageIndex == pageIndex && cached.Revision == _revision)
        {
            return cached.Lines;
        }

        var lines = ReadPlacedLines(pageIndex);
        _placedTextCache = (pageIndex, _revision, lines);
        return lines;
    }

    private List<PlacedLine>? ReadPlacedLines(int pageIndex)
    {
        var text = GetTextPage(pageIndex, GetPage(pageIndex));
        var count = text == IntPtr.Zero ? 0 : PdfiumNative.FPDFText_CountChars(text);
        if (count <= 0)
        {
            return null;
        }

        var chars = new ushort[count + 1];
        PdfiumNative.FPDFText_GetText(text, 0, count, chars);
        if (!chars.AsSpan(0, count).ContainsAnyInRange((ushort)0x0E00, (ushort)0x0E7F))
        {
            return null;
        }

        var stored = RepairedText(text, chars, count) is { } repaired
            ? string.Concat(repaired.Select(e => e.C))
            : new string(chars.Take(count).Select(u => (char)u).ToArray());
        var storedLetters = WithoutSpaces(stored);

        var spans = ReadTextSpans(pageIndex).Spans;
        var thai = spans.Where(s => s.Text.Any(c => c is >= '฀' and <= '๿')).ToList();
        var wrong = thai.Count(s => !storedLetters.Contains(WithoutSpaces(s.Text), StringComparison.Ordinal));
        return wrong >= 2 && wrong * 5 >= thai.Count ? ToLines(spans) : null;
    }

    private static string WithoutSpaces(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>Pieces side by side make a line (top to bottom, then left to right), with a space between them.</summary>
    private static List<PlacedLine> ToLines(IReadOnlyList<PdfTextSpan> spans)
    {
        var rows = new List<List<PdfTextSpan>>();
        foreach (var span in spans.Where(s => s.Text.Trim().Length > 0).OrderBy(s => s.Y))
        {
            var middle = span.Y + (span.Height / 2);
            var row = rows.FirstOrDefault(r => r.Any(o => Math.Abs(o.Y + (o.Height / 2) - middle) < Math.Min(o.Height, span.Height) / 2));
            if (row is null)
            {
                rows.Add([span]);
            }
            else
            {
                row.Add(span);
            }
        }

        var lines = new List<PlacedLine>();
        foreach (var row in rows.OrderBy(r => r.Min(s => s.Y)))
        {
            var placed = new List<PlacedChar>();
            foreach (var span in row.OrderBy(s => s.X))
            {
                if (placed.Count > 0)
                {
                    placed.Add(new PlacedChar(' ', placed[^1].Right, span.X));
                }

                var piece = span.Text.TrimEnd();
                for (var i = 0; i < piece.Length; i++)
                {
                    var left = span.CharLefts is { } lefts && i < lefts.Count ? lefts[i] : span.X + (span.Width * i / piece.Length);

                    // A character ends where the next one (not a mark on it) starts.
                    var right = span.Right;
                    for (var next = i + 1; next < piece.Length; next++)
                    {
                        var nextLeft = span.CharLefts is { } l && next < l.Count ? l[next] : span.X + (span.Width * next / piece.Length);
                        if (nextLeft > left + 0.01)
                        {
                            right = nextLeft;
                            break;
                        }
                    }

                    placed.Add(new PlacedChar(piece[i], left, right));
                }
            }

            lines.Add(new PlacedLine(placed, row.Min(s => s.Y), row.Max(s => s.Bottom)));
        }

        return lines;
    }

    /// <summary>The line at a height on the page: the one it is in, else the nearest.</summary>
    private static int LineAt(List<PlacedLine> lines, double y)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (y >= lines[i].Top && y <= lines[i].Bottom)
            {
                return i;
            }
        }

        return Enumerable.Range(0, lines.Count).MinBy(i => Math.Abs(((lines[i].Top + lines[i].Bottom) / 2) - y));
    }

    /// <summary>The text from one point to another, read line by line as printed.</summary>
    private PdfTextSelection? SelectByPosition(int pageIndex, List<PlacedLine> lines, NormalizedPoint start, NormalizedPoint end)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        var size = PageSizes[pageIndex];
        double x1 = start.X * size.Width, y1 = start.Y * size.Height, x2 = end.X * size.Width, y2 = end.Y * size.Height;
        int first = LineAt(lines, y1), last = LineAt(lines, y2);
        if (first > last || (first == last && x2 < x1))
        {
            (first, last, x1, x2) = (last, first, x2, x1);
        }

        var picked = new List<(int Line, List<PlacedChar> Chars)>();
        for (var line = first; line <= last; line++)
        {
            var chars = lines[line].Chars
                .Where(c => (line > first || c.Right > x1) && (line < last || c.Left < x2))
                .ToList();
            picked.Add((line, chars));
        }

        var text = string.Join("\r\n", picked.Select(p => string.Concat(p.Chars.Select(c => c.C)).Trim()));
        var rects = picked.Where(p => p.Chars.Count > 0).Select(p => Box(size, lines[p.Line], p.Chars)).ToList();
        return rects.Count == 0 ? null : new PdfTextSelection(pageIndex, 0, text.Length, rects, text);
    }

    /// <summary>The text of the characters whose middle is in an area, line by line.</summary>
    private static string TextInAreaByPosition(PdfPageSize size, List<PlacedLine> lines, NormalizedRect area)
    {
        double left = area.X * size.Width, top = area.Y * size.Height;
        double right = left + (area.Width * size.Width), bottom = top + (area.Height * size.Height);
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            var middle = (line.Top + line.Bottom) / 2;
            if (middle < top || middle > bottom)
            {
                continue;
            }

            var inside = string.Concat(line.Chars.Where(c => (c.Left + c.Right) / 2 is var x && x >= left && x <= right).Select(c => c.C)).Trim();
            if (inside.Length > 0)
            {
                text.Append(text.Length > 0 ? "\r\n" : string.Empty).Append(inside);
            }
        }

        return text.ToString();
    }

    /// <summary>Finds <paramref name="query"/> in the lines read by position (a match stays within a line).</summary>
    private IReadOnlyList<PdfTextMatch> FindByPosition(int pageIndex, List<PlacedLine> lines, string query, PdfSearchOptions options)
    {
        var comparison = options.HasFlag(PdfSearchOptions.MatchCase) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var size = PageSizes[pageIndex];
        var matches = new List<PdfTextMatch>();
        var offset = 0;
        foreach (var line in lines)
        {
            var text = line.Text;
            for (var at = query.Length == 0 ? -1 : text.IndexOf(query, comparison); at >= 0; at = text.IndexOf(query, at + query.Length, comparison))
            {
                var end = at + query.Length;
                if (options.HasFlag(PdfSearchOptions.WholeWord)
                    && ((at > 0 && char.IsLetterOrDigit(text[at - 1])) || (end < text.Length && char.IsLetterOrDigit(text[end]))))
                {
                    continue;
                }

                matches.Add(new PdfTextMatch(pageIndex, offset + at, query.Length, [Box(size, line, line.Chars.GetRange(at, query.Length))]));
            }

            offset += text.Length + 1;
        }

        return matches;
    }

    /// <summary>The box around some characters of a line, as a share of the page.</summary>
    private static NormalizedRect Box(PdfPageSize size, PlacedLine line, List<PlacedChar> chars)
    {
        var left = chars.Min(c => c.Left);
        var right = chars.Max(c => c.Right);
        return new NormalizedRect(left / size.Width, line.Top / size.Height, (right - left) / size.Width, (line.Bottom - line.Top) / size.Height);
    }
}
