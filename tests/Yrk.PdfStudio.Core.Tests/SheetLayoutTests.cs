using Yrk.PdfStudio.Core.Pdf.Printing;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>Several pages on a sheet, and booklets.</summary>
public sealed class SheetLayoutTests
{
    private static readonly PrintRect Landscape = new(10, 10, 822, 575);
    private static readonly PrintRect Portrait = new(10, 10, 575, 822);

    [Fact]
    public void Orders_eight_pages_for_a_booklet()
    {
        var order = PrintLayout.BookletOrder(8).Select(p => p + 1);

        Assert.Equal(new int?[] { 8, 1, 2, 7, 6, 3, 4, 5 }, order);
    }

    [Fact]
    public void Fills_a_short_booklet_with_blank_pages()
    {
        // 5 pages make 2 sheets (8 sides of a page): the last three are blank.
        var order = PrintLayout.BookletOrder(5).Select(p => p + 1);

        Assert.Equal(new int?[] { null, 1, 2, null, null, 3, 4, 5 }, order);
    }

    [Fact]
    public void Puts_booklet_pages_on_the_two_halves_of_each_side()
    {
        var pages = new[] { 10, 11, 12, 13 };

        var sides = PrintLayout.Sheets(pages, PagesPerSheet.Booklet, Landscape);

        Assert.Equal(2, sides.Count);
        Assert.Equal(new int?[] { 13, 10 }, sides[0].Select(c => c.PageIndex));
        Assert.Equal(new int?[] { 11, 12 }, sides[1].Select(c => c.PageIndex));
        Assert.Equal(new PrintRect(10, 10, 411, 575), sides[0][0].Area);
        Assert.Equal(new PrintRect(421, 10, 411, 575), sides[0][1].Area);
    }

    [Fact]
    public void Puts_four_pages_in_two_rows()
    {
        var sides = PrintLayout.Sheets([0, 1, 2, 3, 4], PagesPerSheet.Four, Portrait, gap: 5);

        Assert.Equal(2, sides.Count);
        Assert.Equal(4, sides[0].Count);
        Assert.Single(sides[1]);
        Assert.Equal(new PrintRect(10, 10, 285, 408.5), sides[0][0].Area);
        Assert.Equal(new PrintRect(300, 10, 285, 408.5), sides[0][1].Area);
        Assert.Equal(new PrintRect(10, 423.5, 285, 408.5), sides[0][2].Area);
    }

    [Theory]
    [InlineData(PagesPerSheet.Two, true, 2, 1)]
    [InlineData(PagesPerSheet.Two, false, 1, 2)]
    [InlineData(PagesPerSheet.Six, true, 3, 2)]
    [InlineData(PagesPerSheet.Nine, false, 3, 3)]
    public void Lays_pages_out_by_the_shape_of_the_paper(PagesPerSheet layout, bool wide, int columns, int rows)
    {
        Assert.Equal((columns, rows), PrintLayout.Grid(layout, wide ? Landscape : Portrait));
    }
}
