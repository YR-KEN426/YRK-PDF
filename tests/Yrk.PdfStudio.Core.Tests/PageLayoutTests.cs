using System.IO.Compression;
using System.Xml.Linq;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Export;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>Tables, cell colours, merged cells and pictures rebuilt from a page for Word and Excel.</summary>
public sealed class PageLayoutTests : IDisposable
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-layout-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    private async Task<(PdfPageText Text, PdfPageGraphics Graphics)> TablePageAsync()
    {
        var path = TestPdfFactory.CreateTablePdf(Path.Combine(_folder.FullName, "table.pdf"));
        await using var document = await _engine.OpenAsync(path);
        return (await document.GetPageTextSpansAsync(0), await document.GetPageGraphicsAsync(0));
    }

    [Fact]
    public async Task Lines_boxes_and_colours_are_read_from_the_page()
    {
        var (_, graphics) = await TablePageAsync();

        // Table lines and the chart frame, in page points from the top-left.
        Assert.Contains(graphics.Rules, r => r.IsHorizontal && Math.Abs(r.Y1 - 142) < 1 && Math.Abs(r.X1 - 100) < 1 && Math.Abs(r.X2 - 400) < 1);
        Assert.Contains(graphics.Rules, r => !r.IsHorizontal && Math.Abs(r.X1 - 250) < 1 && Math.Abs(r.Y1 - 162) < 1 && Math.Abs(r.Y2 - 202) < 1);
        Assert.Contains(graphics.Graphics, g => g.Fill == new PdfColor(255, 255, 0) && Math.Abs(g.Width - 300) < 1);
        Assert.Contains(graphics.Graphics, g => g.Fill == new PdfColor(0, 0, 255));
    }

    [Fact]
    public async Task A_ruled_table_keeps_its_merged_heading_and_colour()
    {
        var (text, graphics) = await TablePageAsync();
        var blocks = PageLayout.Analyze(text, graphics);

        var table = Assert.Single(blocks.OfType<LayoutTable>());
        Assert.Equal(2, table.ColumnWidths.Count);
        Assert.Equal(3, table.RowHeights.Count);
        var heading = table.Cells.Single(c => c.Row == 0);
        Assert.Equal(2, heading.ColumnSpan);
        Assert.Equal(new PdfColor(255, 255, 0), heading.Fill);
        Assert.Equal(CellBorders.Top | CellBorders.Bottom | CellBorders.Left | CellBorders.Right, heading.Borders);
        Assert.Equal("1,250.00", CellText(table, 1, 1));
        Assert.Equal("Ink", CellText(table, 2, 0));

        // The framed chart is one picture with its label; the label is not left over as text.
        var chart = Assert.Single(blocks.OfType<LayoutPicture>());
        Assert.InRange(chart.Width, 199, 203);
        Assert.DoesNotContain(blocks.OfType<LayoutParagraph>(), p => p.Paragraph.Runs.Any(r => r.Text.Contains("Sales", StringComparison.Ordinal)));
    }

    [Fact]
    public void Lines_only_above_and_below_rows_make_a_table_whose_columns_follow_the_line_pieces()
    {
        // A heading row, a body row; each line drawn in two pieces that meet at x = 200.
        var rules = new List<PdfRule>();
        foreach (var y in new[] { 100.0, 130, 160 })
        {
            rules.Add(new PdfRule(60, y, 199.5, y));
            rules.Add(new PdfRule(200, y, 400, y));
        }

        var spans = new[]
        {
            Span("Period", 70, 110, 40), Span("Average value", 260, 110, 80),
            Span("Before", 70, 140, 40), Span("57.97", 280, 140, 30),
        };
        var blocks = PageLayout.Analyze(new PdfPageText(595, 842, spans), new PdfPageGraphics(595, 842, rules, []));

        var table = Assert.Single(blocks.OfType<LayoutTable>());
        Assert.Equal([140, 200], table.ColumnWidths.Select(w => Math.Round(w)));
        Assert.Equal("57.97", CellText(table, 1, 1));
        Assert.True(table.Cells.Single(c => c.Row == 0 && c.Column == 0).Borders.HasFlag(CellBorders.Top));
        Assert.False(table.Cells.Single(c => c.Row == 0 && c.Column == 0).Borders.HasFlag(CellBorders.Left));
    }

    [Fact]
    public void Text_across_column_lines_is_split_as_a_spreadsheet_shows_it()
    {
        // Columns: number 50–100, name 100–200, spare 200–250, amount 250–330; three rows. The spare column has its
        // line only in the first row, as when a long name runs over it in the other rows.
        var rules = new List<PdfRule>();
        foreach (var y in new[] { 100.0, 115, 130, 145 })
        {
            rules.Add(new PdfRule(50, y, 330, y));
        }

        foreach (var x in new[] { 50.0, 100, 250, 330 })
        {
            rules.Add(new PdfRule(x, 100, x, 145));
        }

        rules.Add(new PdfRule(200, 100, 200, 115));
        // Characters 4 points wide from x = 52: the name starts at 100, right after the number's column line.
        var overflow = Chars("31-00-00-01 Savings account KBANK 031-8-510520 11,190.95", 52, 133, 4);
        var amountStart = overflow.Text.IndexOf("11,190", StringComparison.Ordinal);
        overflow = overflow with
        {
            // The name runs on past the amount's line (hidden there in the PDF); the amount stands well apart.
            CharLefts = overflow.CharLefts!.Select((left, i) => i >= amountStart ? left + 30 : left).ToList(),
            Width = overflow.Width + 30,
        };
        var spans = new[]
        {
            Chars("11-11-00-02 Cash", 52, 103, 4),                          // Number and name in one piece.
            Chars("21-00-00-01 Bank account SCB 468-090938-5", 52, 118, 4), // A long name over the empty spare cell.
            overflow,
        };
        var blocks = PageLayout.Analyze(new PdfPageText(595, 842, spans), new PdfPageGraphics(595, 842, rules, []));

        var table = Assert.Single(blocks.OfType<LayoutTable>());
        Assert.Equal("11-11-00-02", CellText(table, 0, 0));
        Assert.Equal("Cash", CellText(table, 0, 1));
        Assert.Equal("Bank account SCB 468-090938-5", CellText(table, 1, 1));
        Assert.Equal(2, table.Cells.Single(c => c.Row == 1 && c.Column == 1).ColumnSpan); // Runs over the empty spare cell.
        Assert.Equal("Savings account KBANK 031-8-510520", CellText(table, 2, 1));
        Assert.Equal("11,190.95", CellText(table, 2, 3));
    }

    [Fact]
    public void Overlapping_picture_pieces_make_one_picture_and_side_by_side_photos_stay_apart()
    {
        var shapes = new[]
        {
            new PdfGraphic(PdfGraphicKind.Image, 100, 100, 60, 60),
            new PdfGraphic(PdfGraphicKind.Image, 140, 120, 60, 60), // Overlaps the first: pieces of one chart.
            new PdfGraphic(PdfGraphicKind.Image, 300, 100, 60, 60),
            new PdfGraphic(PdfGraphicKind.Image, 362, 100, 60, 60), // Next to the third, not over it.
        };
        var blocks = PageLayout.Analyze(new PdfPageText(595, 842, []), new PdfPageGraphics(595, 842, [], shapes));

        var pictures = blocks.OfType<LayoutPicture>().OrderBy(p => p.X).ToList();
        Assert.Equal(3, pictures.Count);
        Assert.Equal(100, pictures[0].Width);
    }

    [Fact]
    public void Word_may_break_Thai_lines_where_the_PDF_did()
    {
        var black = new PdfColor(0, 0, 0);
        TextRun Run(string text, bool startsLine = false) => new(text, 16, "THSarabunPSK", false, false, black, StartsLine: startsLine);
        var thai = new TextParagraph([Run("การประเมินประสิทธิผล"), Run("ผลิตภัณฑ์", startsLine: true)], 0, 0, false, Left: 72, LineCount: 2, Right: 300);
        var latin = new TextParagraph([Run("Courage + "), Run("Khazaka", startsLine: true)], 0, 0, false, Left: 72, LineCount: 2, Right: 300);
        var path = Path.Combine(_folder.FullName, "lines.docx");
        using (var stream = File.Create(path))
        {
            DocxWriter.Write(stream, [new DocxPage(595, 842, [thai, latin])]);
        }

        using var zip = ZipFile.OpenRead(path);
        var texts = Xml(zip, "word/document.xml").Descendants(W + "t").Select(t => t.Value).ToList();

        // An invisible break mark between the two Thai lines; none where a space already separates the words.
        Assert.Contains("​ผลิตภัณฑ์", texts);
        Assert.Contains("Khazaka", texts);
    }

    [Fact]
    public async Task Word_gets_a_real_table_and_a_picture_where_the_chart_was()
    {
        var (text, graphics) = await TablePageAsync();
        var blocks = PageLayout.Analyze(text, graphics);
        var chart = blocks.OfType<LayoutPicture>().Single();
        var images = new Dictionary<LayoutPicture, DocxImage> { [chart] = new([0x89, 0x50, 0x4E, 0x47], "png", 200, 150) };
        var path = Path.Combine(_folder.FullName, "table.docx");
        using (var stream = File.Create(path))
        {
            DocxWriter.Write(stream, [new DocxPage(595, 842, [], Blocks: blocks, Images: images)]);
        }

        using var zip = ZipFile.OpenRead(path);
        var document = Xml(zip, "word/document.xml");
        var table = Assert.Single(document.Descendants(W + "tbl"));
        Assert.Equal(3, table.Elements(W + "tr").Count());
        Assert.Contains(table.Descendants(W + "gridSpan"), g => (string?)g.Attribute(W + "val") == "2");
        Assert.Contains(table.Descendants(W + "shd"), s => (string?)s.Attribute(W + "fill") == "FFFF00");
        Assert.Contains("1,250.00", string.Concat(table.Descendants(W + "t").Select(t => t.Value)), StringComparison.Ordinal);

        // The chart floats at its place on the page (in EMU: 12 700 per point).
        XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        var anchor = Assert.Single(document.Descendants(wp + "anchor"));
        Assert.Equal((long)Math.Round(chart.X * 12700), long.Parse(anchor.Element(wp + "positionH")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.NotNull(zip.GetEntry("word/media/image1.png"));
    }

    [Fact]
    public async Task Excel_gets_merged_cells_borders_colours_and_number_formats()
    {
        var (text, graphics) = await TablePageAsync();
        var sheet = SpreadsheetLayout.Sheet("Page 1", text, PageLayout.Analyze(text, graphics));
        var path = Path.Combine(_folder.FullName, "table.xlsx");
        using (var stream = File.Create(path))
        {
            XlsxWriter.Write(stream, [sheet]);
        }

        using var zip = ZipFile.OpenRead(path);
        var worksheet = Xml(zip, "xl/worksheets/sheet1.xml");
        Assert.Contains(worksheet.Descendants(S + "mergeCell"), m => ((string?)m.Attribute("ref"))?.EndsWith(":B1", StringComparison.Ordinal) == true);
        var styles = Xml(zip, "xl/styles.xml");
        Assert.Contains(styles.Descendants(S + "fgColor"), c => (string?)c.Attribute("rgb") == "FFFFFF00");
        Assert.Contains(styles.Descendants(S + "xf"), x => (string?)x.Attribute("numFmtId") == "10"); // 15.5%
        Assert.Contains(styles.Descendants(S + "xf"), x => (string?)x.Attribute("numFmtId") == "4");  // 1,250.00
        Assert.Contains(styles.Descendants(S + "left"), l => (string?)l.Attribute("style") == "thin");
    }

    [Theory]
    [InlineData("THSarabunPSK-Bold", "TH SarabunPSK")]
    [InlineData("ABCDEF+THSarabunNew", "TH Sarabun New")]
    [InlineData("AngsanaUPC", "AngsanaUPC")]
    [InlineData("AngsanaNew-Bold", "Angsana New")]
    public void Thai_font_names_are_the_installed_family_names(string pdfName, string family) =>
        Assert.Equal(family, OfficeXml.FontFamily(pdfName));

    [Fact]
    public void No_space_is_put_before_a_Thai_vowel_that_follows_its_letter()
    {
        // "จ" and "ำกัด" in separate pieces with a small gap, as PDFs often store them.
        var line = new[] { Span("บริษัท แอมเรซ จ", 100, 100, 70), Span("ำกัด", 172, 100, 18) };
        var runs = TextLayout.LineRuns(line);
        Assert.Equal("บริษัท แอมเรซ จำกัด", string.Concat(runs.Select(r => r.Text)));
    }

    private static string CellText(LayoutTable table, int row, int column) =>
        string.Concat(table.Cells.Single(c => c.Row == row && c.Column == column).Content
            .OfType<LayoutParagraph>().SelectMany(p => p.Paragraph.Runs).Select(r => r.Text)).Trim();

    private static XDocument Xml(ZipArchive zip, string part)
    {
        using var stream = zip.GetEntry(part)!.Open();
        return XDocument.Load(stream);
    }

    /// <summary>One piece of text with every character <paramref name="advance"/> points wide.</summary>
    private static PdfTextSpan Chars(string text, double x, double y, double advance, double size = 8) =>
        new(text, x, y, text.Length * advance, size * 1.2, size, "Tahoma", false, false, new PdfColor(0, 0, 0),
            Enumerable.Range(0, text.Length).Select(i => x + (i * advance)).ToList());

    private static PdfTextSpan Span(string text, double x, double y, double width, double size = 11) =>
        new(text, x, y, width, size * 1.2, size, "Tahoma", false, false, new PdfColor(0, 0, 0));
}
