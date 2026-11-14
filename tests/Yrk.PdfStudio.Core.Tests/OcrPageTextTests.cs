using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Export;
using Yrk.PdfStudio.Core.Pdf.Ocr;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class OcrPageTextTests
{
    private static OcrWord Word(string text, int line = 0, float confidence = 90, float lineConfidence = 90,
        double x = 0.1, double y = 0.1, double height = 0.02, bool spaceAfter = false) =>
        new(text, new NormalizedRect(x, y, 0.1, height), confidence, spaceAfter,
            line, 0.02, lineConfidence);

    [Fact]
    public void Garbage_filter_uses_line_confidence_and_keeps_numbers_and_Thai_prose()
    {
        var page = new OcrPageResult(0,
        [
            Word("a", 0, 20, 74), Word("al", 0, 30, 74), Word("๐", 0, 40, 74),
            Word("วันที่", 1, 40, 70), Word("12", 1, 90, 70), Word("พฤศจิกายน", 1, 90, 70),
            Word("ยอดรวม", 2, 40, 70), Word("123", 2, 90, 70), Word("๑๒๓๔", 2, 90, 70),
        ]);

        Assert.Equal(["วันที่", "12", "พฤศจิกายน", "ยอดรวม", "123", "๑๒๓๔"],
            OcrPageText.FilterWords(page).Select(w => w.Text));
        Assert.Equal(9, page.Words.Count);
        Assert.Equal(6, OcrPageText.ToPageText(page, 600, 800).Spans.Count);
    }

    [Fact]
    public void Body_lines_share_one_font_size_headings_keep_theirs_and_low_stray_marks_go()
    {
        static OcrWord Line(string text, int line, double lineHeight) =>
            new(text, new NormalizedRect(0.1, 0.1 + line * 0.05, 0.1, lineHeight), 90, false, line, lineHeight, 95);
        // Heights in points on an 800 pt page: body lines 14 and 15, heading 24, stray marks 4.
        var page = new OcrPageResult(0,
        [
            Line("หัวเรื่อง", 0, 24 / 800.0),
            Line("a", 1, 4 / 800.0), Line("๐", 1, 4 / 800.0),
            Line("บรรทัดหนึ่ง", 2, 14 / 800.0), Line("12", 2, 14 / 800.0),
            Line("บรรทัดสอง", 3, 15 / 800.0), Line("บรรทัดสาม", 4, 15 / 800.0),
        ]);

        var spans = OcrPageText.ToPageText(page, 600, 800).Spans;
        Assert.Equal(["หัวเรื่อง", "บรรทัดหนึ่ง", "12", "บรรทัดสอง", "บรรทัดสาม"], spans.Select(s => s.Text));
        Assert.Equal(26.5, spans[0].FontSize);
        Assert.All(spans.Skip(1), s => Assert.Equal(16.5, s.FontSize));
    }

    [Theory]
    [InlineData(74.99f, 3, false)]
    [InlineData(75f, 3, true)]
    [InlineData(74f, 2, true)]
    public void Garbage_thresholds_are_strict_for_confidence_and_inclusive_for_short_fraction(
        float lineConfidence, int shortCount, bool retained)
    {
        var page = new OcrPageResult(0, Enumerable.Range(0, 5)
            .Select(i => Word(i < shortCount ? "ab" : "long", lineConfidence: lineConfidence)).ToArray());
        Assert.Equal(retained ? 5 : 0, OcrPageText.FilterWords(page).Count);
    }

    [Fact]
    public void Review_indices_refer_to_original_words_after_filtering_and_exclude_deleted_words()
    {
        var duplicate = Word("repeat", 1, 59);
        var page = new OcrPageResult(4,
        [
            Word("x", 0, 15, 30), duplicate, Word("แน่ใจ", 1, 60), duplicate,
            Word("", 1, 10), Word(" ", 1, 10), Word("มั่นใจ", 1, 90),
        ]);
        Assert.True(OcrPageText.GetUncertainWordIndices(page).SetEquals([1, 3]));
        Assert.Same(duplicate, OcrPageText.FilterWords(page)[0]);
    }

    [Fact]
    public void Conversion_preserves_boxes_order_spaces_and_one_rounded_font_size_per_line()
    {
        var page = new OcrPageResult(3,
        [
            Word("ภาษาไทย", x: 0.1, height: 0.018, spaceAfter: true),
            Word("English", x: 0.201, height: 0.014),
            Word("ต่อท้าย", x: 0.302),
            Word("บรรทัดสอง", 1, y: 0.125),
        ]);
        var text = OcrPageText.ToPageText(page, 600, 800);
        Assert.Equal(600, text.Width);
        Assert.Equal(800, text.Height);
        Assert.Equal(["ภาษาไทย ", "English", "ต่อท้าย", "บรรทัดสอง"], text.Spans.Select(s => s.Text));
        var first = text.Spans[0];
        Assert.Equal(60, first.X);
        Assert.Equal(80, first.Y);
        Assert.Equal(60, first.Width);
        Assert.Equal(14.4, first.Height, 6);
        Assert.All(text.Spans, s =>
        {
            Assert.Equal("THSarabunNew", s.FontName);
            Assert.Equal(17.5, s.FontSize); // 16 pt line height * 1.1, rounded to 0.5 pt.
        });
        Assert.Equal("ภาษาไทย Englishต่อท้าย\nบรรทัดสอง",
            TextLayout.PlainText(text).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Old_four_argument_words_remain_usable_without_line_metadata()
    {
        var word = new OcrWord("a", new NormalizedRect(0.1, 0.1, 0.1, 0.02), 20, false);
        var page = new OcrPageResult(0, [word]);
        Assert.Same(word, Assert.Single(OcrPageText.FilterWords(page)));
        Assert.Equal(17.5, Assert.Single(OcrPageText.ToPageText(page, 600, 800).Spans).FontSize);
        Assert.True(OcrPageText.GetUncertainWordIndices(page).SetEquals([0]));
    }

    [Fact]
    public void Line_box_can_supply_height_and_empty_pages_remain_empty()
    {
        var word = Word("ไทย") with { LineHeight = 0, LineBounds = new NormalizedRect(0.1, 0.1, 0.3, 0.025) };
        Assert.Equal(22, Assert.Single(OcrPageText.ToPageText(new(0, [word]), 600, 800).Spans).FontSize);
        var empty = new OcrPageResult(0, []);
        Assert.Empty(OcrPageText.ToPageText(empty, 600, 800).Spans);
        Assert.Empty(OcrPageText.GetUncertainWordIndices(empty));
    }

    [Fact]
    public async Task Pdf_layer_bytes_ignore_line_metadata_and_keep_garbage_words()
    {
        var folder = Directory.CreateTempSubdirectory("yrk-ocr-layer-");
        try
        {
            var path = TestPdfFactory.CreateLayeredPdf(Path.Combine(folder.FullName, "source.pdf"));
            var pdf = File.ReadAllBytes(path);
            var original = new OcrWord("xy", new NormalizedRect(0.1, 0.1, 0.1, 0.02), 30, true);
            var enriched = original with { Line = 0, LineHeight = 0.03, LineConfidence = 20 };
            Assert.Empty(OcrPageText.FilterWords(new(0, [enriched])));
            var expected = PdfOcr.AddTextLayer(pdf, [new(0, [original])]);
            var actual = PdfOcr.AddTextLayer(pdf, [new(0, [enriched])]);
            Assert.Equal(expected, actual);
            var output = Path.Combine(folder.FullName, "searchable.pdf");
            File.WriteAllBytes(output, actual);
            using var engine = new PdfiumEngine();
            await using var document = await engine.OpenAsync(output);
            Assert.Contains("xy", await document.GetPageTextAsync(0), StringComparison.Ordinal);
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task Recognize_with_no_selected_pages_returns_original_bytes_without_language_data()
    {
        var folder = Directory.CreateTempSubdirectory("yrk-ocr-noop-");
        try
        {
            var path = TestPdfFactory.CreateReportPdf(Path.Combine(folder.FullName, "report.pdf"));
            var pdf = File.ReadAllBytes(path);
            using var engine = new PdfiumEngine();
            await using var document = await engine.OpenAsync(path);
            var data = new OcrLanguageData(folder.FullName);
            Assert.Empty(await PdfOcr.RecognizePagesAsync(document, new(["eng"]), data));
            var result = await PdfOcr.RecognizeAsync(document, pdf, new(["eng"]), data);
            Assert.Same(pdf, result.Output);
            Assert.Equal(0, result.PagesRecognised);
            Assert.Equal(document.PageCount, result.PagesSkipped);
            Assert.Equal(0, result.Words);
            await Assert.ThrowsAsync<PdfException>(() => PdfOcr.RecognizeOrientedPagesAsync(document, pdf, new(["eng"]), data));
        }
        finally { folder.Delete(recursive: true); }
    }
}
