using Yrk.PdfStudio.Core.Pdf.Ocr;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class OcrReviewEditsTests
{
    [Fact]
    public async Task Mixed_real_render_mode_keeps_visible_and_old_hidden_text()
    {
        var original = ScanPdfFixture.Build("BT /F1 12 Tf 10 300 Td 3 Tr (old) Tj 0.0 Tr (Visible) Tj ET");
        var result = OcrReviewEdits.RemoveExistingPdfLayers(original, [0]);
        await using var app = new AppTestHarness();
        File.WriteAllBytes(app.PathFor("mixed.pdf"), result);
        await using var document = await app.Engine.OpenAsync(app.PathFor("mixed.pdf"));
        var text = await document.GetPageTextAsync(0);
        Assert.Contains("Visible", text);
        Assert.Contains("old", text);
    }

    [Theory]
    [InlineData("/GS1 gs")]
    [InlineData("/bad Tr")]
    [InlineData("0 1 Tr")]
    [InlineData("(bad) Tc")]
    [InlineData("[120 /bad] TJ")]
    [InlineData("Unknown")]
    [InlineData("Q")]
    [InlineData("q")]
    [InlineData("42")]
    [InlineData(") q Q")]
    public void Unsupported_layer_removal_throws_PdfException_without_modifying_input(string operation)
    {
        var bytes = ScanPdfFixture.Build("BT /F1 12 Tf 3 Tr (old hidden words) Tj ET " + operation,
            extGStateFont: true);
        var original = bytes.ToArray();
        Assert.Throws<PdfException>(() => OcrReviewEdits.RemoveExistingPdfLayers(bytes, [0]));
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Normal_ocr_only_layer_is_removed_including_real_render_mode_and_nested_forms(bool form)
    {
        var bytes = ScanPdfFixture.Build("q 0.2 g 20 30 40 50 re f Q BT /F1 12 Tf 3.0 Tr (old hidden words) Tj ET",
            form: form, pages: 2);
        await using var app = new AppTestHarness();
        File.WriteAllBytes(app.PathFor("original.pdf"), bytes);
        File.WriteAllBytes(app.PathFor("removed.pdf"), OcrReviewEdits.RemoveExistingPdfLayers(bytes, [0]));
        await using var original = await app.Engine.OpenAsync(app.PathFor("original.pdf"));
        await using var document = await app.Engine.OpenAsync(app.PathFor("removed.pdf"));
        Assert.True(string.IsNullOrWhiteSpace(await document.GetPageTextAsync(0)));
        Assert.Equal(await original.GetPageTextAsync(1), await document.GetPageTextAsync(1));
        for (var index = 0; index < 2; index++)
            Assert.Equal((await original.RenderPageAsync(index, 300, 400)).Pixels,
                (await document.RenderPageAsync(index, 300, 400)).Pixels);
    }

    private static OcrWord Word(string text, int line = 0, float lineConfidence = 90) =>
        new(text, new(.1, .1, .2, .04), 30, true, line, .04, lineConfidence);

    [Fact]
    public void Replacements_and_deletions_use_original_indices_even_for_the_same_record_instance()
    {
        var duplicate = Word("duplicate");
        var page = new OcrPageResult(3, [Word("x", 1, 30), duplicate, duplicate, Word("delete"), Word("keep")]);
        Assert.True(OcrPageText.GetUncertainWordIndices(page).SetEquals([1, 2, 3, 4]));
        var result = OcrReviewEdits.Apply(page, new Dictionary<int, string?> { [2] = "corrected", [3] = null });
        Assert.Equal(["x", "duplicate", "corrected", "keep"], result.ForPdf.Words.Select(w => w.Text));
        Assert.Equal(["duplicate", "corrected", "keep"], result.ForText.Words.Select(w => w.Text));
        Assert.Equal(100, result.ForPdf.Words[2].Confidence);
        Assert.Equal("duplicate", page.Words[2].Text);
        Assert.Equal(5, page.Words.Count);
        Assert.Same(duplicate, result.ForPdf.Words[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Deleting_a_long_word_does_not_make_a_retained_line_disappear(string? deletion)
    {
        var page = new OcrPageResult(0, [Word("ab", lineConfidence: 70), Word("cd", lineConfidence: 70),
            Word("longone", lineConfidence: 70), Word("longtwo", lineConfidence: 70)]);
        var result = OcrReviewEdits.Apply(page, new Dictionary<int, string?> { [3] = deletion });
        Assert.Empty(OcrPageText.FilterWords(result.ForPdf)); // Refiltering would wrongly drop the whole line.
        Assert.Equal(["ab ", "cd ", "longone "],
            OcrPageText.ToPageText(result.ForText, 300, 400, filtered: false).Spans.Select(s => s.Text));
    }

    [Fact]
    public void Editing_garbage_does_not_restore_a_filtered_line_and_no_edits_preserve_text_exactly()
    {
        var page = new OcrPageResult(0, [Word("x", 1, 30), Word("original")]);
        var result = OcrReviewEdits.Apply(page, new Dictionary<int, string?> { [0] = "longreplacement" });
        Assert.Equal(["original"], result.ForText.Words.Select(w => w.Text));
        Assert.Equal(["longreplacement", "original"], result.ForPdf.Words.Select(w => w.Text));
        var unchanged = OcrReviewEdits.Apply(page, new Dictionary<int, string?>());
        Assert.Equal(page.Words, unchanged.ForPdf.Words);
        Assert.Equal(OcrPageText.ToPageText(page, 300, 400).Spans,
            OcrPageText.ToPageText(unchanged.ForText, 300, 400, filtered: false).Spans);
    }

    [Fact]
    public async Task Mixed_text_object_is_not_rewritten_and_preserves_visible_text_position()
    {
        var source = CosDocument.Load(ScanPdfFixture.Build(""));
        var update = new CosIncrementalUpdate(source);
        var edit = new CosPageEdit(source, source.GetPages()[0]);
        var font = edit.AddResource("Font", "Hidden", GlyphlessFont.AddTo(update));
        edit.Append($"BT /{font} 14 Tf 1 0 0 1 10 300 Tm 3 Tr <0068006900640064> Tj /F1 12 Tf 0 Tr (Visible) Tj ET");
        edit.Commit(update);
        var original = update.Build().Bytes;
        await AssertMixedPagePreserved(original, [0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_nested_forms_on_mixed_pages_remain_unchanged(bool form)
    {
        var original = ScanPdfFixture.Build("q BT /F1 12 Tf 3 Tr 10 300 Td (Hidden) Tj ET Q BT /F1 12 Tf 10 250 Td (Visible) Tj ET",
            form: form, pages: 2);
        await AssertMixedPagePreserved(original, [0]);
    }

    private static async Task AssertMixedPagePreserved(byte[] original, int[] editedPages)
    {
        Assert.Same(original, OcrReviewEdits.RemoveExistingPdfLayers(original, []));
        var folder = Directory.CreateTempSubdirectory("ocr-layer-");
        try
        {
            var first = Path.Combine(folder.FullName, "original.pdf");
            var second = Path.Combine(folder.FullName, "reviewed.pdf");
            File.WriteAllBytes(first, original);
            File.WriteAllBytes(second, OcrReviewEdits.RemoveExistingPdfLayers(original, editedPages));
            using var engine = new PdfiumEngine();
            await using var before = await engine.OpenAsync(first);
            await using var after = await engine.OpenAsync(second);
            for (var page = 0; page < before.PageCount; page++)
            {
                var oldText = await before.GetPageTextAsync(page);
                var text = await after.GetPageTextAsync(page);
                Assert.Equal(oldText, text); // Mixed pages retain both layers.
                Assert.Equal((await before.RenderPageAsync(page, 600, 800)).Pixels, (await after.RenderPageAsync(page, 600, 800)).Pixels);
            }
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public async Task Pdf_layer_contains_replacement_and_preserves_unfiltered_words()
    {
        var page = new OcrPageResult(0, [Word("x", 1, 30), Word("original"), Word("deleted")]);
        var result = OcrReviewEdits.Apply(page, new Dictionary<int, string?> { [1] = "corrected", [2] = null });
        var folder = Directory.CreateTempSubdirectory("ocr-edits-");
        try
        {
            var path = Path.Combine(folder.FullName, "result.pdf");
            await File.WriteAllBytesAsync(path, PdfOcr.AddTextLayer(ScanPdfFixture.Build(""), [result.ForPdf]));
            using var engine = new PdfiumEngine();
            await using var document = await engine.OpenAsync(path);
            var text = await document.GetPageTextAsync(0);
            Assert.Contains("corrected", text);
            Assert.Contains("x", text);
            Assert.DoesNotContain("original", text);
            Assert.DoesNotContain("deleted", text);
        }
        finally { folder.Delete(true); }
    }
}
