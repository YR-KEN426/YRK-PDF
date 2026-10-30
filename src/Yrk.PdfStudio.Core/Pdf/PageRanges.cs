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

    /// <summary>
    /// One part per bookmark down to <paramref name="levels"/> deep (1 = top level only): from the bookmark's page to
    /// the page before the next one. Pages before the first bookmark become a part of their own; bookmarks that point
    /// nowhere, or to the same page as an earlier one, are skipped.
    /// </summary>
    public static IReadOnlyList<(string Title, IReadOnlyList<int> Pages)> ByBookmarks(IReadOnlyList<PdfBookmark> bookmarks, int pageCount, int levels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        var starts = new SortedDictionary<int, string>();
        void Collect(IEnumerable<PdfBookmark> items, int depth)
        {
            foreach (var bookmark in items)
            {
                if (bookmark.PageIndex is { } page && page >= 0 && page < pageCount)
                {
                    starts.TryAdd(page, bookmark.Title.Trim());
                }

                if (depth < levels)
                {
                    Collect(bookmark.Children, depth + 1);
                }
            }
        }

        Collect(bookmarks, 1);
        if (starts.Count > 0 && !starts.ContainsKey(0))
        {
            starts[0] = string.Empty; // The pages before the first bookmark (a cover, for example).
        }

        var firsts = starts.Keys.ToList();
        return firsts
            .Select((first, i) => (starts[first], (IReadOnlyList<int>)Enumerable.Range(first, (i + 1 < firsts.Count ? firsts[i + 1] : pageCount) - first).ToList()))
            .ToList();
    }

    /// <summary>How many levels deep the bookmarks go.</summary>
    public static int BookmarkDepth(IReadOnlyList<PdfBookmark> bookmarks) =>
        bookmarks.Count == 0 ? 0 : 1 + bookmarks.Max(b => BookmarkDepth(b.Children));

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
