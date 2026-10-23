using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cad;
using Yrk.PdfStudio.Core.Pdf.Compare;
using Yrk.PdfStudio.Core.Pdf.Measure;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class MeasureCompareTests : IDisposable
{
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-measure-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task Reads_the_scale_of_a_converted_drawing()
    {
        var dxf = Path.Combine(_folder.FullName, "plan.dxf");
        var drawing = new ACadSharp.CadDocument();
        drawing.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Millimeters;
        drawing.Entities.Add(new ACadSharp.Entities.Line(new CSMath.XYZ(0, 0, 0), new CSMath.XYZ(30000, 0, 0)));
        drawing.Entities.Add(new ACadSharp.Entities.Line(new CSMath.XYZ(0, 0, 0), new CSMath.XYZ(0, 10000, 0)));
        ACadSharp.IO.DxfWriter.Write(dxf, drawing);
        var result = CadToPdf.Convert(dxf);
        var path = Path.Combine(_folder.FullName, "plan.pdf");
        File.WriteAllBytes(path, result.Pdf);

        await using var document = await _engine.OpenAsync(path);
        var scale = Assert.Single(await document.GetPageScalesAsync(0));

        Assert.Equal("1:50", scale.Ratio);
        Assert.Equal("mm", scale.Unit);
        Assert.Null(scale.Area);

        // The 30 m line, measured on the page, is 30 000 mm.
        var lengthPoints = 30000.0 / 50 * 72 / 25.4;
        Assert.Equal(30000, lengthPoints * scale.UnitsPerPoint, 1);
    }

    [Fact]
    public void Measures_lengths_and_areas()
    {
        (double, double)[] square = [(0, 0), (10, 0), (10, 10), (0, 10)];

        Assert.Equal(30, Measurement.Length(square));
        Assert.Equal(40, Measurement.Length(square, closed: true));
        Assert.Equal(100, Measurement.Area(square));
        Assert.Equal(25.4, Measurement.Millimetres("in"));
        Assert.Null(Measurement.Millimetres("furlong"));
    }

    [Fact]
    public void Finds_added_and_removed_content_but_ignores_tiny_shifts()
    {
        var first = Blank(200, 100);
        var second = Blank(200, 100);
        DrawRect(first, 10, 10, 80, 2);   // A line in both, shifted by one pixel in the second.
        DrawRect(second, 11, 10, 80, 2);
        DrawRect(second, 120, 40, 30, 30); // A box only in the second.
        DrawRect(first, 20, 70, 10, 10);   // A square only in the first.

        var result = PdfCompare.Diff(first, second);

        Assert.Equal(2, result.Regions.Count);
        Assert.Contains(result.Regions, r => r.X <= 120 && r.X + r.Width >= 150 && r.Y <= 40 && r.Y + r.Height >= 70);
        Assert.True(result.AddedPixels >= 900);
        Assert.True(result.RemovedPixels >= 100);

        // Added ink is blue, removed red, the shared line grey.
        Assert.True(Pixel(result.Overlay, 135, 55).B > 150 && Pixel(result.Overlay, 135, 55).R < 100);
        Assert.True(Pixel(result.Overlay, 25, 75).R > 150 && Pixel(result.Overlay, 25, 75).B < 100);
        var shared = Pixel(result.Overlay, 50, 10);
        Assert.Equal(shared.R, shared.B);
    }

    [Fact]
    public void Reports_no_changes_for_the_same_page()
    {
        var page = Blank(50, 50);
        DrawRect(page, 5, 5, 20, 20);

        Assert.False(PdfCompare.Diff(page, page).HasChanges);
    }

    private static PdfBitmap Blank(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        return new PdfBitmap(width, height, width * 4, pixels);
    }

    private static void DrawRect(PdfBitmap bitmap, int x, int y, int width, int height)
    {
        for (var row = y; row < y + height; row++)
        {
            bitmap.Pixels.AsSpan((row * bitmap.Stride) + (x * 4), width * 4).Clear();
        }
    }

    private static (int R, int G, int B) Pixel(PdfBitmap bitmap, int x, int y)
    {
        var i = (y * bitmap.Stride) + (x * 4);
        return (bitmap.Pixels[i + 2], bitmap.Pixels[i + 1], bitmap.Pixels[i]);
    }
}
