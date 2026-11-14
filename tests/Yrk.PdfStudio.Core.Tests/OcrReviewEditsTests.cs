using Yrk.PdfStudio.Core.Pdf.Ocr;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class OcrReviewEditsTests
{
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
    public async Task Replacing_hidden_text_preserves_visible_text_position_even_in_the_same_text_object()
    {
        var source = CosDocument.Load(ScanPdfFixture.Build(""));
        var update = new CosIncrementalUpdate(source);
        var edit = new CosPageEdit(source, source.GetPages()[0]);
        var font = edit.AddResource("Font", "Hidden", GlyphlessFont.AddTo(update));
        edit.Append($"BT /{font} 14 Tf 1 0 0 1 10 300 Tm 3 Tr <0068006900640064> Tj /F1 12 Tf 0 Tr (Visible) Tj ET");
        edit.Commit(update);
        var original = update.Build().Bytes;
        await AssertReplacement(original, [0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacing_shared_nested_forms_does_not_change_unedited_pages_or_visible_ink(bool form)
    {
        var original = ScanPdfFixture.Build("q BT /F1 12 Tf 3 Tr 10 300 Td (Hidden) Tj ET Q BT /F1 12 Tf 10 250 Td (Visible) Tj ET",
            form: form, pages: 2);
        await AssertReplacement(original, [0]);
    }

    private static async Task AssertReplacement(byte[] original, int[] editedPages)
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
                if (editedPages.Contains(page)) Assert.Equal("Visible", text.Trim());
                else Assert.Equal(oldText, text);
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
