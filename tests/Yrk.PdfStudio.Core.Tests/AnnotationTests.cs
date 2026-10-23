using System.Text;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AnnotationTests : IDisposable
{
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");
    private static readonly PdfColor Yellow = new(255, 230, 0);
    private static readonly PdfColor Red = new(220, 30, 30);

    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-annots-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task Selects_text_between_two_points()
    {
        await using var document = await _engine.OpenAsync(SamplePath);
        // Search rectangles can be per character (this PDF positions each glyph), so use their union.
        var rects = (await document.FindTextAsync(0, "invoice", PdfSearchOptions.None))[0].Rects;
        var left = rects.Min(r => r.X);
        var right = rects.Max(r => r.X + r.Width);
        var middle = rects.Average(r => r.Y + (r.Height / 2));

        var selection = await document.GetTextSelectionAsync(0, new NormalizedPoint(left + 0.001, middle), new NormalizedPoint(right - 0.001, middle));

        Assert.NotNull(selection);
        Assert.Equal("invoice", selection.Text);
        Assert.NotEmpty(selection.Rects);
    }

    [Fact]
    public async Task Returns_no_selection_on_empty_paper()
    {
        await using var document = await _engine.OpenAsync(SamplePath);

        Assert.Null(await document.GetTextSelectionAsync(0, new NormalizedPoint(0.5, 0.9), new NormalizedPoint(0.6, 0.95)));
    }

    [Fact]
    public async Task Creates_every_kind_with_an_appearance_and_saves_them()
    {
        var target = PathFor("annotated.pdf");
        await using (var document = await _engine.OpenAsync(SamplePath))
        {
            var word = (await document.FindTextAsync(0, "invoice", PdfSearchOptions.None))[0].Rects;
            await document.AddAnnotationAsync(0, new PdfTextMarkupSpec(PdfAnnotationKind.Highlight, word, Yellow, "Check this", "Ken"));
            await document.AddAnnotationAsync(0, new PdfNoteSpec(new NormalizedPoint(0.8, 0.3), Yellow, "หมายเหตุภาษาไทย", "Ken"));
            await document.AddAnnotationAsync(0, new PdfInkSpec([[new(0.2, 0.6), new(0.3, 0.65), new(0.4, 0.6)]], 2, Red));
            await document.AddAnnotationAsync(0, new PdfShapeSpec(PdfAnnotationKind.Square, new NormalizedRect(0.5, 0.55, 0.2, 0.1), 2, Red));
            await document.AddAnnotationAsync(0, new PdfShapeSpec(PdfAnnotationKind.Circle, new NormalizedRect(0.5, 0.7, 0.2, 0.1), 2, Red));
            await document.AddAnnotationAsync(0, new PdfArrowSpec(new NormalizedPoint(0.1, 0.85), new NormalizedPoint(0.4, 0.8), 2, Red));
            await document.SaveAsync(target);
        }

        // Every new annotation has an appearance stream, so other viewers draw it too.
        var raw = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(target));
        Assert.True(CountOccurrences(raw, "/AP") >= 6);

        await using var reopened = await _engine.OpenAsync(target);
        var annotations = await reopened.GetAnnotationsAsync(0);
        Assert.Equal(
            [PdfAnnotationKind.Highlight, PdfAnnotationKind.Note, PdfAnnotationKind.Ink, PdfAnnotationKind.Square, PdfAnnotationKind.Circle, PdfAnnotationKind.Ink],
            annotations.Select(a => a.Kind));
        Assert.All(annotations, a => Assert.Equal(32, a.Id.Length));
        Assert.Equal("Check this", annotations[0].Contents);
        Assert.Equal("หมายเหตุภาษาไทย", annotations[1].Contents);
        Assert.Equal("Ken", annotations[1].Author);
        Assert.StartsWith("D:", annotations[1].Modified);

        // The red square outline is drawn on the page.
        var bitmap = await reopened.RenderPageAsync(0, 595, 842);
        var edge = ((int)(0.55 * 842) * bitmap.Stride) + ((int)(0.6 * 595) * 4);
        Assert.True(bitmap.Pixels[edge + 2] > 150 && bitmap.Pixels[edge] < 120, "expected red at the top edge of the square");
    }

    [Fact]
    public async Task Edits_and_removes_annotations_by_id()
    {
        await using var document = await _engine.OpenAsync(SamplePath);
        var first = await document.AddAnnotationAsync(0, new PdfNoteSpec(new NormalizedPoint(0.1, 0.1), Yellow, "first"));
        var second = await document.AddAnnotationAsync(0, new PdfNoteSpec(new NormalizedPoint(0.2, 0.2), Yellow, "second"));

        await document.SetAnnotationContentsAsync(new PdfAnnotationRef(0, -1, second), "edited");
        await document.RemoveAnnotationAsync(new PdfAnnotationRef(0, -1, first));

        var remaining = await document.GetAnnotationsAsync(0);
        var note = Assert.Single(remaining);
        Assert.Equal(second, note.Id);
        Assert.Equal("edited", note.Contents);
        await Assert.ThrowsAsync<PdfException>(() => document.RemoveAnnotationAsync(new PdfAnnotationRef(0, -1, first)));
    }

    [Fact]
    public async Task Annotations_on_rotated_pages_stay_where_they_were_drawn()
    {
        await using var document = await _engine.OpenAsync(SamplePath);
        await document.RotatePagesAsync([0], 1);

        await document.AddAnnotationAsync(0, new PdfShapeSpec(PdfAnnotationKind.Square, new NormalizedRect(0.1, 0.1, 0.2, 0.3), 2, Red));

        var square = Assert.Single(await document.GetAnnotationsAsync(0));
        Assert.Equal(0.1, square.Bounds.X, 2);
        Assert.Equal(0.1, square.Bounds.Y, 2);
        Assert.Equal(0.2, square.Bounds.Width, 2);
        Assert.Equal(0.3, square.Bounds.Height, 2);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private string PathFor(string name) => Path.Combine(_folder.FullName, name);
}
