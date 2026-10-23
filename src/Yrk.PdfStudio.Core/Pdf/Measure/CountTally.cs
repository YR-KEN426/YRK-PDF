using System.Globalization;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Export;

namespace Yrk.PdfStudio.Core.Pdf.Measure;

/// <summary>One counted item: its type (for example "Door") and the 0-based page it is on.</summary>
public readonly record struct CountedItem(string Type, int PageIndex);

/// <summary>How many of one type are on one page; <see cref="PageIndex"/> null is the type's total.</summary>
public sealed record CountRow(string Type, int? PageIndex, int Count);

/// <summary>Totals for the count tool: per type and page, per type, and overall.</summary>
public static class CountTally
{
    /// <summary>Rows per type (in the given order), each type's pages first and then its total.</summary>
    public static IReadOnlyList<CountRow> Rows(IEnumerable<CountedItem> items, IReadOnlyList<string> typeOrder)
    {
        var list = items.ToList();
        var rows = new List<CountRow>();
        foreach (var type in typeOrder.Concat(list.Select(i => i.Type)).Distinct())
        {
            var ofType = list.Where(i => i.Type == type).ToList();
            if (ofType.Count == 0)
            {
                continue;
            }

            rows.AddRange(ofType.GroupBy(i => i.PageIndex).OrderBy(g => g.Key).Select(g => new CountRow(type, g.Key, g.Count())));
            rows.Add(new CountRow(type, null, ofType.Count));
        }

        return rows;
    }

    /// <summary>"Door 12 · Window 8 · Total 20" (only types with items).</summary>
    public static string Summary(IEnumerable<CountedItem> items, IReadOnlyList<string> typeOrder, string totalWord)
    {
        var totals = Rows(items, typeOrder).Where(r => r.PageIndex is null).ToList();
        if (totals.Count == 0)
        {
            return string.Empty;
        }

        var parts = totals.Select(t => $"{t.Type} {t.Count.ToString(CultureInfo.CurrentCulture)}").ToList();
        if (totals.Count > 1)
        {
            parts.Add($"{totalWord} {totals.Sum(t => t.Count).ToString(CultureInfo.CurrentCulture)}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>A table (header row first) for the clipboard or Excel: Type, Page, Count; totals in the Page column.</summary>
    public static IReadOnlyList<IReadOnlyList<string?>> Table(IReadOnlyList<CountRow> rows, string typeHeader, string pageHeader, string countHeader, string totalWord)
    {
        var table = new List<IReadOnlyList<string?>> { new[] { typeHeader, pageHeader, countHeader } };
        foreach (var row in rows)
        {
            table.Add([row.Type, row.PageIndex is { } page ? (page + 1).ToString(CultureInfo.InvariantCulture) : totalWord, row.Count.ToString(CultureInfo.InvariantCulture)]);
        }

        var grand = rows.Where(r => r.PageIndex is null).Sum(r => r.Count);
        table.Add([totalWord, null, grand.ToString(CultureInfo.InvariantCulture)]);
        return table;
    }

    /// <summary>Tab-separated lines: pastes into Excel as cells.</summary>
    public static string ToTabSeparated(IReadOnlyList<IReadOnlyList<string?>> table)
    {
        var text = new StringBuilder();
        foreach (var row in table)
        {
            text.Append(string.Join('\t', row.Select(cell => cell ?? string.Empty))).Append("\r\n");
        }

        return text.ToString();
    }

    public static XlsxSheet ToSheet(string name, IReadOnlyList<IReadOnlyList<string?>> table) =>
        new(name, [140, 50, 50], table);
}
