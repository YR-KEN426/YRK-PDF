using System.Globalization;
using System.Text;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>Edited text must come out the size it was, however the PDF set that size.</summary>
public sealed class TextEditSizeTests : IDisposable
{
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-editsize-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Theory]
    [InlineData(1, 24)]  // Size 1, scaled by the text matrix (browsers, printers, converters).
    [InlineData(24, 1)]  // Size 24, no scaling.
    [InlineData(12, 2)]  // Both.
    public async Task The_size_on_the_page_is_read_whatever_sets_it(double tf, double scale)
    {
        var path = ScaledPdf(Path.Combine(_folder.FullName, "scaled.pdf"), tf, scale);
        await using var document = await _engine.OpenAsync(path);

        var line = await document.GetTextLineAtAsync(0, new NormalizedPoint(0.15, 1 - (705.0 / 842)));

        Assert.NotNull(line);
        Assert.Equal(24, line.FontSize, 1);
    }

    [Fact]
    public async Task Edited_Thai_and_English_lines_keep_their_height()
    {
        await using var document = await _engine.OpenAsync(SamplePath); // Printed from Edge: drawn at 0.75 pt per pixel.
        var pageHeight = document.PageSizes[0].Height;
        foreach (var y in new[] { 0.12, 0.20, 0.24 })
        {
            var line = await document.GetTextLineAtAsync(0, new NormalizedPoint(0.2, y));
            Assert.NotNull(line);

            var family = App.ViewModels.MainViewModel.ResolveFamily(line.FontName);
            var blocks = Sta.Run(() =>
            {
                var shaped = TextShaper.Shape(line.Text, family, line.IsBold, line.IsItalic, line.FontSize);
                var topLeft = new NormalizedPoint(line.BaselineStart.X, line.BaselineStart.Y - (shaped.Baseline / pageHeight));
                return TextShaper.ToBlocks([(0, topLeft, line.Color, shaped)]);
            });
            var output = Path.Combine(_folder.FullName, $"edited-{y.ToString("0.00", CultureInfo.InvariantCulture)}.pdf");
            File.WriteAllBytes(output, PdfTextEditor.Apply(File.ReadAllBytes(SamplePath), [new PdfTextEdit(0, line.Bounds, blocks[0])]));

            await using var edited = await _engine.OpenAsync(output);
            var after = await edited.GetTextLineAtAsync(0, new NormalizedPoint(line.Bounds.X + 0.01, line.Bounds.Y + (line.Bounds.Height / 2)));
            Assert.NotNull(after);
            Assert.Equal(line.FontSize, after.FontSize, 0);
            Assert.Equal(line.Bounds.Height * pageHeight, after.Bounds.Height * pageHeight, 0); // Within half a point.
        }
    }

    [Theory]
    [InlineData("Tahoma")]
    [InlineData("Courier New")]
    public async Task A_substitute_font_is_sized_to_look_as_tall(string substitute)
    {
        await using var document = await _engine.OpenAsync(SamplePath);
        var pageHeight = document.PageSizes[0].Height;
        foreach (var y in new[] { 0.12, 0.24 }) // An English heading and a Thai line.
        {
            var line = await document.GetTextLineAtAsync(0, new NormalizedPoint(0.2, y));
            Assert.NotNull(line);

            var size = Sta.Run(() => App.ViewModels.MainViewModel.MatchSize(line, substitute, pageHeight));
            var blocks = Sta.Run(() =>
            {
                var shaped = TextShaper.Shape(line.Text, substitute, line.IsBold, line.IsItalic, size);
                var topLeft = new NormalizedPoint(line.BaselineStart.X, line.BaselineStart.Y - (shaped.Baseline / pageHeight));
                return TextShaper.ToBlocks([(0, topLeft, line.Color, shaped)]);
            });
            var output = Path.Combine(_folder.FullName, $"substitute-{substitute}-{y.ToString("0.00", CultureInfo.InvariantCulture)}.pdf");
            File.WriteAllBytes(output, PdfTextEditor.Apply(File.ReadAllBytes(SamplePath), [new PdfTextEdit(0, line.Bounds, blocks[0])]));

            await using var edited = await _engine.OpenAsync(output);
            var after = await edited.GetTextLineAtAsync(0, new NormalizedPoint(line.Bounds.X + 0.01, line.Bounds.Y + (line.Bounds.Height / 2)));
            Assert.NotNull(after);
            var before = line.Bounds.Height * pageHeight;
            Assert.InRange(after.Bounds.Height * pageHeight, before * 0.85, before * 1.15);
        }
    }

    private static string ScaledPdf(string path, double tf, double scale)
    {
        var content = FormattableString.Invariant($"BT /F1 {tf} Tf {scale} 0 0 {scale} 72 700 Tm (Hello World 123) Tj ET");
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.Latin1);
        return path;
    }
}
