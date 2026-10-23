using System.Globalization;

namespace Yrk.PdfStudio.Core.Pdf;

/// <summary>Parses page ranges as people type them: "1-3, 5, 8-" (1-based, inclusive).</summary>
public static class PageRanges
{
    /// <summary>
    /// Returns one list of 0-based page indices per comma-separated part. "8-" runs to the last page,
    /// "-3" starts at the first, and "5-3" counts down.
    /// </summary>
    /// <exception cref="FormatException">The text is not a valid range list for this page count.</exception>
    public static IReadOnlyList<IReadOnlyList<int>> Parse(string text, int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        var parts = (text ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new FormatException("Enter page numbers, for example 1-3, 5, 8-.");
        }

        var ranges = new List<IReadOnlyList<int>>();
        foreach (var part in parts)
        {
            var dash = part.IndexOfAny(['-', '–']);
            int first, last;
            if (dash < 0)
            {
                first = last = ParseNumber(part, pageCount);
            }
            else
            {
                var left = part[..dash].Trim();
                var right = part[(dash + 1)..].Trim();
                first = left.Length == 0 ? 1 : ParseNumber(left, pageCount);
                last = right.Length == 0 ? pageCount : ParseNumber(right, pageCount);
            }

            var step = first <= last ? 1 : -1;
            var pages = new List<int>();
            for (var page = first; page != last + step; page += step)
            {
                pages.Add(page - 1);
            }

            ranges.Add(pages);
        }

        return ranges;
    }

    /// <summary>Splits into consecutive parts of <paramref name="pagesPerPart"/> pages.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> EveryNPages(int pageCount, int pagesPerPart)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pagesPerPart);
        return Enumerable.Range(0, pageCount).Chunk(pagesPerPart).Select(chunk => (IReadOnlyList<int>)chunk).ToList();
    }

    private static int ParseNumber(string text, int pageCount)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            throw new FormatException($"\"{text}\" is not a page number.");
        }

        if (number < 1 || number > pageCount)
        {
            throw new FormatException($"Page {number} does not exist. The document has {pageCount} pages.");
        }

        return number;
    }
}
