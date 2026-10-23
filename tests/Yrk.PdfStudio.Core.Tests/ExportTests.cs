using System.IO.Compression;
using System.Xml.Linq;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Export;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ExportTests : IDisposable
{
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-export-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    private async Task<PdfPageText> ReportTextAsync()
    {
        var path = TestPdfFactory.CreateReportPdf(Path.Combine(_folder.FullName, "report.pdf"));
        await using var document = await _engine.OpenAsync(path);
        return await document.GetPageTextSpansAsync(0);
    }

    [Fact]
    public async Task Text_spans_carry_position_size_and_bold()
    {
        var page = await ReportTextAsync();

        var heading = Assert.Single(page.Spans, s => s.Text.Contains("Monthly report", StringComparison.Ordinal));
        Assert.True(heading.Bold);
        Assert.Equal(18, heading.FontSize, 0);
        Assert.InRange(heading.X, 70, 74);
        Assert.InRange(heading.Y, 842 - 760 - 18, 842 - 760); // Top-left origin.
        Assert.False(page.Spans.Single(s => s.Text.StartsWith("Paper", StringComparison.Ordinal)).Bold);
    }

    [Fact]
    public async Task Lines_are_joined_into_paragraphs_and_table_rows_stay_apart()
    {
        var paragraphs = TextLayout.Paragraphs(await ReportTextAsync());
        var texts = paragraphs.Select(p => string.Concat(p.Runs.Select(r => r.Text))).ToList();

        Assert.Equal("Monthly report", texts[0]);
        Assert.Equal("The figures below show the costs for the month and are checked by the accounts team.", texts[1]);
        Assert.Equal("Paper\t1,250.00", texts[3]);
        Assert.Contains(paragraphs[3].Runs, r => r.IsTab);
        Assert.True(paragraphs[2].SpaceBefore > 10); // The gap above the table is kept.
    }

    [Fact]
    public async Task Table_puts_right_aligned_numbers_in_one_column()
    {
        var table = TextLayout.Table(await ReportTextAsync());

        Assert.Equal(2, table.ColumnWidths.Count);
        var rows = table.Rows.Where(r => r[1] is not null).ToList();
        Assert.Equal(["Amount", "1,250.00", "980.50", "2,230.50"], rows.Select(r => r[1]));
        Assert.Equal(["Item", "Paper", "Ink", "Total"], rows.Select(r => r[0]));
        Assert.Contains(table.Rows, r => r[0] == "Monthly report" && r[1] is null);
    }

    [Fact]
    public async Task Thai_marks_stay_with_their_letters()
    {
        const string text = "สวัสดีครับ ผู้ใช้งาน น้ำใจ";
        var blocks = Sta.Run(() => App.Imaging.TextShaper.ToBlocks([(0, new NormalizedPoint(0.1, 0.9), new PdfColor(0, 0, 0), App.Imaging.TextShaper.Shape(text, "Leelawadee UI", false, false, 18))]));
        var path = Path.Combine(_folder.FullName, "thai.pdf");
        File.WriteAllBytes(path, Yrk.PdfStudio.Core.Pdf.Text.PdfTextWriter.AddText(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf")), blocks));
        await using var document = await _engine.OpenAsync(path);

        var lines = TextLayout.PlainText(await document.GetPageTextSpansAsync(0)).Split('\n');

        Assert.Contains(lines, l => l.Contains("สวัสดีครับ ผู้ใช้งาน", StringComparison.Ordinal));
    }

    [Fact]
    public void Thai_lines_are_joined_without_a_space()
    {
        var page = new PdfPageText(595, 842,
        [
            Span("สัญญาฉบับนี้ทำขึ้นระหว่างผู้ให้เช่าและผู้เช่าโดยมีรายละเอียดดังต่อไปนี้", 72, 100, 451),
            Span("ทั้งสองฝ่ายตกลงกัน", 72, 116, 120),
        ]);

        var paragraph = Assert.Single(TextLayout.Paragraphs(page));
        Assert.Equal("สัญญาฉบับนี้ทำขึ้นระหว่างผู้ให้เช่าและผู้เช่าโดยมีรายละเอียดดังต่อไปนี้ทั้งสองฝ่ายตกลงกัน", string.Concat(paragraph.Runs.Select(r => r.Text)));
    }

    [Fact]
    public void A_title_in_the_middle_is_centred()
    {
        var page = new PdfPageText(595, 842,
        [
            Span("CONTRACT", 260, 80, 75, 16),
            Span("This agreement is made between the parties named below in Bangkok.", 72, 120, 451),
        ]);

        var paragraphs = TextLayout.Paragraphs(page);
        Assert.True(paragraphs[0].Centered);
        Assert.False(paragraphs[1].Centered);
    }

    [Theory]
    [InlineData("1,250.00", 1250)]
    [InlineData("-12", -12)]
    [InlineData("(500)", -500)]
    [InlineData("12.5%", 0.125)]
    [InlineData("0.75", 0.75)]
    public void Numbers_are_read_as_numbers(string text, double expected)
    {
        Assert.True(XlsxWriter.TryParseNumber(text, out var value));
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData("007")]
    [InlineData("0812345678")]
    [InlineData("1,25")]
    [InlineData("12/10/2569")]
    [InlineData("A-001")]
    public void Codes_and_dates_stay_text(string text) => Assert.False(XlsxWriter.TryParseNumber(text, out _));

    [Fact]
    public void Column_names_follow_excel()
    {
        Assert.Equal("A", XlsxWriter.ColumnName(0));
        Assert.Equal("Z", XlsxWriter.ColumnName(25));
        Assert.Equal("AA", XlsxWriter.ColumnName(26));
        Assert.Equal("AZ", XlsxWriter.ColumnName(51));
    }

    [Theory]
    [InlineData("ABCDEF+THSarabunNew-Bold", "TH Sarabun New")]
    [InlineData("TimesNewRomanPSMT", "Times New Roman")]
    [InlineData("Arial,Bold", "Arial")]
    [InlineData("ArialMT", "Arial")]
    [InlineData("Helvetica", "Helvetica")]
    public void Pdf_font_names_become_family_names(string pdfName, string family) =>
        Assert.Equal(family, OfficeXml.FontFamily(pdfName));

    [Fact]
    public void PowerPoint_file_has_a_slide_per_page_at_the_page_size()
    {
        var picture = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var path = Path.Combine(_folder.FullName, "slides.pptx");
        using (var stream = File.Create(path))
        {
            PptxWriter.Write(stream, [new PptxSlide(picture, 595, 842), new PptxSlide(picture, 842, 595)]);
        }

        using var zip = ZipFile.OpenRead(path);
        XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        var presentation = Xml(zip, "ppt/presentation.xml");
        Assert.Equal(2, presentation.Descendants(p + "sldId").Count());
        var size = presentation.Descendants(p + "sldSz").Single();
        Assert.Equal("7556500", (string?)size.Attribute("cx")); // The first page's width: 595 pt.
        foreach (var part in new[] { "ppt/slides/slide1.xml", "ppt/slides/slide2.xml", "ppt/media/image2.jpeg", "ppt/slideMasters/slideMaster1.xml", "ppt/theme/theme1.xml" })
        {
            Assert.NotNull(zip.GetEntry(part));
        }

        // The landscape page is fitted into the portrait slide, centred.
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var offset = Xml(zip, "ppt/slides/slide2.xml").Descendants(a + "off").Last();
        Assert.Equal("0", (string?)offset.Attribute("x"));
        Assert.True(long.Parse((string)offset.Attribute("y")!, System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public async Task Word_file_is_a_valid_package_with_the_text_and_styles()
    {
        var page = await ReportTextAsync();
        var picture = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var path = Path.Combine(_folder.FullName, "report.docx");
        using (var stream = File.Create(path))
        {
            DocxWriter.Write(stream, [new DocxPage(page.Width, page.Height, TextLayout.Paragraphs(page)), new DocxPage(595, 842, [], new DocxImage(picture, "jpeg", 100, 141))]);
        }

        using var zip = ZipFile.OpenRead(path);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("word/media/image1.jpg"));
        var document = Xml(zip, "word/document.xml");
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var text = string.Concat(document.Descendants(w + "t").Select(t => t.Value));
        Assert.Contains("Monthly report", text, StringComparison.Ordinal);
        Assert.Contains("checked by the accounts team.", text, StringComparison.Ordinal);
        Assert.NotEmpty(document.Descendants(w + "b"));                              // Bold heading.
        Assert.Contains(document.Descendants(w + "sz"), s => (string?)s.Attribute(w + "val") == "36"); // 18 pt.
        Assert.Single(document.Descendants(w + "pageBreakBefore"));                  // The second page.
        Assert.Contains("rIdPic1", Xml(zip, "word/_rels/document.xml.rels").ToString(), StringComparison.Ordinal);
        _ = Xml(zip, "word/styles.xml");
    }

    [Fact]
    public async Task Excel_file_has_numbers_as_numbers()
    {
        var table = TextLayout.Table(await ReportTextAsync());
        var path = Path.Combine(_folder.FullName, "report.xlsx");
        using (var stream = File.Create(path))
        {
            XlsxWriter.Write(stream, [new XlsxSheet("Page 1", table.ColumnWidths, table.Rows), new XlsxSheet("Page 1", [], [["x"]])]);
        }

        using var zip = ZipFile.OpenRead(path);
        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var workbook = Xml(zip, "xl/workbook.xml");
        Assert.Equal(["Page 1", "Page 1 (2)"], workbook.Descendants(s + "sheet").Select(e => (string?)e.Attribute("name")));
        var sheet = Xml(zip, "xl/worksheets/sheet1.xml");
        var numbers = sheet.Descendants(s + "c").Where(c => c.Attribute("t") is null).Select(c => c.Element(s + "v")!.Value).ToList();
        Assert.Equal(["1250", "980.5", "2230.5"], numbers);
        Assert.Contains(sheet.Descendants(s + "t"), t => t.Value == "Paper");
        _ = Xml(zip, "xl/styles.xml");
    }

    [Fact]
    public async Task Plain_text_keeps_lines_and_gaps()
    {
        var text = TextLayout.PlainText(await ReportTextAsync());
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        Assert.Equal("Monthly report", lines[0]);
        Assert.Equal(string.Empty, lines[1]);
        Assert.Contains("Paper\t1,250.00", lines);
    }

    [Fact]
    public async Task Exporter_writes_images_at_the_chosen_resolution()
    {
        var path = TestPdfFactory.CreateReportPdf(Path.Combine(_folder.FullName, "report.pdf"));
        await using var document = await _engine.OpenAsync(path);
        var output = Path.Combine(_folder.FullName, "report.png");

        var result = await App.Services.PdfExporter.ExportAsync(
            document, new App.Services.ExportSettings(App.Services.ExportFormat.Png, [0], Dpi: 150), output, new Progress<int>(), default);

        Assert.Equal([output], result.Files);
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(new MemoryStream(File.ReadAllBytes(output)));
        Assert.Equal(595 * 150 / 72, frame.PixelWidth, 1.0);
        Assert.Equal(150, frame.DpiX, 0);
        Assert.False(File.Exists(output + ".yrk-tmp"));
    }

    [Fact]
    public async Task Pages_without_text_go_into_word_as_pictures()
    {
        var path = TestPdfFactory.CreateLayeredPdf(Path.Combine(_folder.FullName, "drawing.pdf"));
        await using var document = await _engine.OpenAsync(path);
        var output = Path.Combine(_folder.FullName, "drawing.docx");

        var result = await App.Services.PdfExporter.ExportAsync(
            document, new App.Services.ExportSettings(App.Services.ExportFormat.Word, [0]), output, new Progress<int>(), default);

        Assert.Equal(1, result.PicturePages);
        using var zip = ZipFile.OpenRead(output);
        Assert.True(zip.GetEntry("word/media/image1.jpg")!.Length > 1000);
    }

    [Fact]
    public void Image_files_are_numbered_so_they_sort()
    {
        var files = App.Services.PdfExporter.ImagePaths(@"C:\out\plan.png", [0, 9, 119], 120);
        Assert.Equal([@"C:\out\plan-001.png", @"C:\out\plan-010.png", @"C:\out\plan-120.png"], files);
        Assert.Equal([@"C:\out\plan.png"], App.Services.PdfExporter.ImagePaths(@"C:\out\plan.png", [4], 120));
    }

    private static XDocument Xml(ZipArchive zip, string part)
    {
        using var stream = zip.GetEntry(part)!.Open();
        return XDocument.Load(stream);
    }

    private static PdfTextSpan Span(string text, double x, double y, double width, double size = 11) =>
        new(text, x, y, width, size * 1.2, size, "Tahoma", false, false, new PdfColor(0, 0, 0));
}
