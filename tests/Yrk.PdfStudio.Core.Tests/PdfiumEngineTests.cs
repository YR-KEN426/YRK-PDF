using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>
/// Runs against tests/Assets/sample.pdf: 3 A4 pages printed from sample.html with headings
/// "Chapter 1..3" (exported as bookmarks), "invoice" on every page and Thai text "สัญญา" on every page.
/// </summary>
public sealed class PdfiumEngineTests : IDisposable
{
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");

    private readonly PdfiumEngine _engine = new();

    public void Dispose() => _engine.Dispose();

    [Fact]
    public async Task Opens_document_and_reads_page_sizes()
    {
        await using var document = await _engine.OpenAsync(SamplePath);

        Assert.Equal(3, document.PageCount);
        Assert.All(document.PageSizes, size =>
        {
            Assert.Equal(595, size.Width, 0); // A4 in points
            Assert.Equal(842, size.Height, 0);
        });
    }

    [Fact]
    public async Task Renders_page_with_content_on_white_background()
    {
        await using var document = await _engine.OpenAsync(SamplePath);

        var bitmap = await document.RenderPageAsync(0, 400, 566);

        Assert.Equal(400, bitmap.Width);
        Assert.Equal(566, bitmap.Height);
        Assert.Equal((byte)255, bitmap.Pixels[0]); // corner is white paper
        Assert.Contains(bitmap.Pixels.Where((_, i) => i % 4 != 3), value => value < 128); // some dark text
    }

    [Fact]
    public async Task Reads_bookmarks_with_page_targets()
    {
        await using var document = await _engine.OpenAsync(SamplePath);

        var bookmarks = Flatten(await document.GetBookmarksAsync()).ToList();

        Assert.Contains(bookmarks, b => b.Title.StartsWith("Chapter 1", StringComparison.Ordinal) && b.PageIndex == 0);
        Assert.Contains(bookmarks, b => b.Title.StartsWith("Chapter 3", StringComparison.Ordinal) && b.PageIndex == 2);
    }

    [Fact]
    public async Task Finds_text_on_every_page_with_normalized_rectangles()
    {
        await using var document = await _engine.OpenAsync(SamplePath);

        for (var page = 0; page < document.PageCount; page++)
        {
            var matches = await document.FindTextAsync(page, "invoice", PdfSearchOptions.None);

            Assert.NotEmpty(matches);
            Assert.All(matches.SelectMany(m => m.Rects), rect =>
            {
                Assert.InRange(rect.X, 0, 1);
                Assert.InRange(rect.Y, 0, 1);
                Assert.InRange(rect.Width, 0.0001, 1);
                Assert.InRange(rect.Height, 0.0001, 1);
            });
        }
    }

    [Fact]
    public async Task Finds_thai_text()
    {
        await using var document = await _engine.OpenAsync(SamplePath);

        var matches = await document.FindTextAsync(0, "ภาษาไทย", PdfSearchOptions.None);

        Assert.NotEmpty(matches);
    }

    // Chrome/Edge "Print to PDF" wraps Thai clusters such as "สั" in one /ActualText span covering
    // two glyphs, and PDFium applies that text to each glyph, so extraction yields "สัญสั ญา".
    // The repeats are left out (RepeatedText), and such pages are searched without them.
    [Fact]
    public async Task Finds_thai_text_with_combining_marks_in_chromium_pdfs()
    {
        await using var document = await _engine.OpenAsync(SamplePath);

        var matches = await document.FindTextAsync(1, "สัญญา", PdfSearchOptions.None);
        var text = await document.GetPageTextAsync(1);
        var spans = await document.GetPageTextSpansAsync(1);

        var match = Assert.Single(matches.Where(m => m.Rects.Count > 0).Take(1));
        Assert.All(match.Rects, r => Assert.True(r.Width > 0 && r.Height > 0));
        Assert.Contains("สัญญา", text, StringComparison.Ordinal);
        Assert.DoesNotContain("สัญสั", text, StringComparison.Ordinal);
        Assert.Contains(spans.Spans, s => s.Text.Contains("สัญญา", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Extracts_page_text()
    {
        await using var document = await _engine.OpenAsync(SamplePath);

        var text = await document.GetPageTextAsync(1);

        Assert.Contains("Chapter 2: Contract", text);
        Assert.Contains("12,500.00", text);
    }

    [Fact]
    public async Task Match_case_is_respected()
    {
        await using var document = await _engine.OpenAsync(SamplePath);

        var matches = await document.FindTextAsync(0, "INVOICE", PdfSearchOptions.MatchCase);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task Opens_files_in_folders_with_thai_names()
    {
        var folder = Directory.CreateTempSubdirectory("yrk-");
        try
        {
            var thaiFolder = Directory.CreateDirectory(Path.Combine(folder.FullName, "เอกสาร ทดสอบ"));
            var path = Path.Combine(thaiFolder.FullName, "สัญญา.pdf");
            File.Copy(SamplePath, path);

            await using var document = await _engine.OpenAsync(path);

            Assert.Equal(3, document.PageCount);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Rejects_files_that_are_not_pdf()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "this is not a pdf");

            await Assert.ThrowsAsync<PdfException>(() => _engine.OpenAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Cancelled_render_is_not_executed()
    {
        await using var document = await _engine.OpenAsync(SamplePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => document.RenderPageAsync(0, 100, 100, cancellationToken: cancellation.Token));
    }

    private static IEnumerable<PdfBookmark> Flatten(IEnumerable<PdfBookmark> bookmarks) =>
        bookmarks.SelectMany(b => new[] { b }.Concat(Flatten(b.Children)));
}
