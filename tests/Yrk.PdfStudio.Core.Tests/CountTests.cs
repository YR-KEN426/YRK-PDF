using Yrk.PdfStudio.Core.Pdf.Measure;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class CountTests
{
    private static readonly CountedItem[] Items =
    [
        new("Door", 0), new("Window", 0), new("Door", 1), new("Door", 0), new("Window", 2),
    ];

    [Fact]
    public void Rows_list_each_type_by_page_then_its_total_in_the_chosen_order()
    {
        var rows = CountTally.Rows(Items, ["Window", "Door", "Light"]);

        Assert.Equal(
            [
                new CountRow("Window", 0, 1), new CountRow("Window", 2, 1), new CountRow("Window", null, 2),
                new CountRow("Door", 0, 2), new CountRow("Door", 1, 1), new CountRow("Door", null, 3),
            ],
            rows);
    }

    [Fact]
    public void Summary_shows_types_with_items_and_a_total()
    {
        Assert.Equal("Door 3 · Window 2 · Total 5", CountTally.Summary(Items, ["Door", "Window", "Light"], "Total"));
        Assert.Equal("Door 1", CountTally.Summary([new("Door", 0)], ["Door"], "Total"));
        Assert.Equal(string.Empty, CountTally.Summary([], ["Door"], "Total"));
    }

    [Fact]
    public void Table_has_a_header_one_based_pages_and_a_grand_total()
    {
        var table = CountTally.Table(CountTally.Rows(Items, ["Door", "Window"]), "Type", "Page", "Count", "Total");

        Assert.Equal(["Type", "Page", "Count"], table[0]);
        Assert.Equal(["Door", "1", "2"], table[1]);
        Assert.Equal(["Door", "Total", "3"], table[3]);
        Assert.Equal(["Total", null, "5"], table[^1]);
        Assert.StartsWith("Type\tPage\tCount\r\nDoor\t1\t2\r\n", CountTally.ToTabSeparated(table), StringComparison.Ordinal);
    }
}
