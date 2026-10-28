using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class TextWriterTests : IDisposable
{
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");

    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-text-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task Writes_searchable_Thai_text_with_an_embedded_font()
    {
        const string text = "สวัสดีครับ ผู้ใช้งาน น้ำใจ Hello 123";
        var blocks = Sta.Run(() => TextShaper.ToBlocks([(0, new NormalizedPoint(0.1, 0.7), new PdfColor(0, 0, 160), TextShaper.Shape(text, "Leelawadee UI", false, false, 18))]));

        var output = PdfTextWriter.AddText(File.ReadAllBytes(SamplePath), blocks);
        var path = Path.Combine(_folder.FullName, "text.pdf");
        File.WriteAllBytes(path, output);

        await using var document = await _engine.OpenAsync(path);
        var pageText = await document.GetPageTextAsync(0);
        Assert.Contains("สวัสดีครับ", pageText, StringComparison.Ordinal);
        Assert.Contains("ผู้ใช้งาน", pageText, StringComparison.Ordinal);
        Assert.Contains("น้ำใจ", pageText, StringComparison.Ordinal); // Stacked marks: outlines over invisible text.
        Assert.Contains("Hello 123", pageText, StringComparison.Ordinal);

        var match = Assert.Single(await document.FindTextAsync(0, "Hello", PdfSearchOptions.None));
        Assert.InRange(match.Rects[0].Y, 0.68, 0.72);

        var line = await document.GetTextLineAtAsync(0, new NormalizedPoint(match.Rects[0].X + 0.005, match.Rects[0].Y + (match.Rects[0].Height / 2)));
        Assert.NotNull(line);
        Assert.Equal(18, line.FontSize, 0);
        Assert.Contains("Leelawadee", line.FontName, StringComparison.Ordinal);
        Assert.Equal(new PdfColor(0, 0, 160), line.Color);
    }

    [Fact]
    public async Task Editing_takes_one_cell_of_a_table_row()
    {
        // One row of a table: PDFium reads it as one line, but the cells are far apart.
        var blocks = Sta.Run(() => TextShaper.ToBlocks(
        [
            (0, new NormalizedPoint(0.1, 0.5), new PdfColor(0, 0, 0), TextShaper.Shape("Floor plan and 3D design", "Leelawadee UI", false, false, 11)),
            (0, new NormalizedPoint(0.6, 0.5), new PdfColor(0, 0, 0), TextShaper.Shape("1", "Leelawadee UI", false, false, 11)),
            (0, new NormalizedPoint(0.8, 0.5), new PdfColor(0, 0, 0), TextShaper.Shape("25,000", "Leelawadee UI", false, false, 11)),
        ]));
        var path = Path.Combine(_folder.FullName, "row.pdf");
        File.WriteAllBytes(path, PdfTextWriter.AddText(File.ReadAllBytes(SamplePath), blocks));

        await using var document = await _engine.OpenAsync(path);
        var plan = Assert.Single(await document.FindTextAsync(0, "plan", PdfSearchOptions.None)).Rects[0];
        var line = await document.GetTextLineAtAsync(0, new NormalizedPoint(plan.X + 0.002, plan.Y + (plan.Height / 2)));
        Assert.NotNull(line);
        Assert.Equal("Floor plan and 3D design", line.Text);
        Assert.True(line.Bounds.X + line.Bounds.Width < 0.6);

        var price = Assert.Single(await document.FindTextAsync(0, "25,000", PdfSearchOptions.None)).Rects[0];
        line = await document.GetTextLineAtAsync(0, new NormalizedPoint(price.X + 0.002, price.Y + (price.Height / 2)));
        Assert.Equal("25,000", line?.Text);
    }

    [Fact]
    public async Task Editing_reads_Thai_as_copying_does()
    {
        // The tone mark stored before the vowel, and a stray space before a sara am (as some fonts make PDFium
        // read it): the editor must get the words as they read, like copying.
        var blocks = Sta.Run(() => TextShaper.ToBlocks([(0, new NormalizedPoint(0.1, 0.7), new PdfColor(0, 0, 0), TextShaper.Shape("ข้อความน้ีสำคัญประจ ำปี", "Leelawadee UI", false, false, 18))]));
        var path = Path.Combine(_folder.FullName, "edit-thai.pdf");
        File.WriteAllBytes(path, PdfTextWriter.AddText(File.ReadAllBytes(SamplePath), blocks));

        await using var document = await _engine.OpenAsync(path);
        var box = Assert.Single(await document.FindTextAsync(0, "สำคัญ", PdfSearchOptions.None)).Rects[0];
        var line = await document.GetTextLineAtAsync(0, new NormalizedPoint(box.X + 0.002, box.Y + (box.Height / 2)));
        Assert.Equal("ข้อความนี้สำคัญประจำปี", line?.Text);
    }

    [Fact]
    public async Task Search_and_copy_read_Thai_marks_in_written_order()
    {
        // Stored the way some PDFs do: the tone mark before the vowel above ("น้ี" for "นี้").
        const string stored = "ข้อความน้ีสำคัญ";
        var blocks = Sta.Run(() => TextShaper.ToBlocks([(0, new NormalizedPoint(0.1, 0.7), new PdfColor(0, 0, 0), TextShaper.Shape(stored, "Leelawadee UI", false, false, 18))]));
        var path = Path.Combine(_folder.FullName, "marks.pdf");
        File.WriteAllBytes(path, PdfTextWriter.AddText(File.ReadAllBytes(SamplePath), blocks));

        await using var document = await _engine.OpenAsync(path);
        Assert.Contains("น้ี", await document.GetPageTextAsync(0), StringComparison.Ordinal); // As stored.

        var match = Assert.Single(await document.FindTextAsync(0, "ความนี้สำคัญ", PdfSearchOptions.None));
        var box = match.Rects[0];
        var middle = box.Y + (box.Height / 2);
        var selection = await document.GetTextSelectionAsync(0, new NormalizedPoint(box.X + 0.001, middle), new NormalizedPoint(box.X + box.Width - 0.001, middle));
        Assert.NotNull(selection);
        Assert.Contains("นี้สำคัญ", selection.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replaces_a_line_of_text()
    {
        PdfTextLine line;
        await using (var document = await _engine.OpenAsync(SamplePath))
        {
            var match = (await document.FindTextAsync(0, "appears", PdfSearchOptions.None))[0].Rects[0];
            line = (await document.GetTextLineAtAsync(0, new NormalizedPoint(match.X + 0.002, match.Y + (match.Height / 2))))!;
        }

        Assert.StartsWith("appears", line.Text, StringComparison.Ordinal);
        var replacement = Sta.Run(() =>
        {
            var shaped = TextShaper.Shape("ปรากฏอยู่หลายหน้า (edited)", "Leelawadee UI", false, false, line.FontSize);
            var page = new PdfPageSize(595.28, 841.89);
            var top = new NormalizedPoint(line.BaselineStart.X, line.BaselineStart.Y - (shaped.Baseline / page.Height));
            return TextShaper.ToBlocks([(0, top, line.Color, shaped)])[0];
        });

        var output = PdfTextEditor.Apply(File.ReadAllBytes(SamplePath), [new PdfTextEdit(0, line.Bounds, replacement)]);
        var path = Path.Combine(_folder.FullName, "edited.pdf");
        File.WriteAllBytes(path, output);

        await using var edited = await _engine.OpenAsync(path);
        var text = await edited.GetPageTextAsync(0);
        Assert.DoesNotContain("appears on several pages", text, StringComparison.Ordinal);
        Assert.Contains("(edited)", text, StringComparison.Ordinal);
        Assert.Contains("This sample", text, StringComparison.Ordinal);
    }
}

internal static class Sta
{
    public static T Run<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return error is null ? result : throw new InvalidOperationException("STA work failed: " + error.Message, error);
    }
}
