using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cad;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class CadTests : IDisposable
{
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-cad-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task Converts_a_drawing_with_layers_blocks_and_text()
    {
        var dxf = CreateDrawing();

        var result = CadToPdf.Convert(dxf);

        Assert.Equal("1:1", result.Scale);
        Assert.Empty(result.Warnings);
        Assert.Contains(result.Texts, t => t.Text == "PANEL A" && Math.Abs(t.Rotation) < 0.01);
        Assert.Contains(result.Texts, t => t.Text == "Ø50" && Math.Abs(t.Rotation - 90) < 0.01);

        // Text as the app writes it: real, searchable text on its layer.
        var blocks = Sta.Run(() => CadText.Shape(result.Texts, result.Pages));
        var pdf = PdfFiles.Compact(PdfTextWriter.AddText(result.Pdf, blocks));
        var path = Path.Combine(_folder.FullName, "drawing.pdf");
        File.WriteAllBytes(path, pdf);

        await using var document = await _engine.OpenAsync(path);
        var layers = await document.GetLayersAsync();
        Assert.Equal(["DIM", "WALL"], layers.Select(l => l.Name).Order());
        Assert.False(layers.Single(l => l.Name == "DIM").IsVisibleByDefault); // Off in the drawing.
        Assert.Contains("PANEL A", await document.GetPageTextAsync(0), StringComparison.Ordinal);

        // The 200 × 100 mm rectangle at 1:1, inside a 10 mm margin: page = 220 × 120 mm (plus the circle).
        Assert.Equal(220 * 72 / 25.4, document.PageSizes[0].Width, 0);

        // The red wall line is drawn where expected (10 mm from the left edge, 10 mm up).
        var bitmap = await document.RenderPageAsync(0, (int)(document.PageSizes[0].Width * 2), (int)(document.PageSizes[0].Height * 2));
        var x = (int)(60 * 72 / 25.4 * 2);
        var y = bitmap.Height - (int)(10 * 72 / 25.4 * 2);
        var found = Enumerable.Range(y - 3, 7).Any(row =>
        {
            var i = (row * bitmap.Stride) + (x * 4);
            return bitmap.Pixels[i + 2] > 200 && bitmap.Pixels[i + 1] < 80;
        });
        Assert.True(found, "The red wall line is not where it should be.");
    }

    [Fact]
    public async Task Layouts_become_pages_with_viewports_at_their_scale()
    {
        var result = CadToPdf.Convert(CreateDrawingWithLayout());

        Assert.Equal(["Model", "A3 Plan"], result.Pages.Select(p => p.Name));
        var sheet = result.Pages[1];
        Assert.True(sheet.IsLayout);
        Assert.Equal((420 + 10) * 72 / 25.4, sheet.Width, 0); // The paper border plus a 5 mm margin.

        // Paper text on the sheet; model text seen through the viewport; frozen layer and far-away text left out.
        Assert.Contains(result.Texts, t => t.Text == "SHEET A3" && t.PageIndex == 1);
        Assert.Contains(result.Texts, t => t.Text == "ROOM" && t.PageIndex == 1);
        Assert.DoesNotContain(result.Texts, t => t.Text == "NOTE" && t.PageIndex == 1);
        Assert.Contains(result.Texts, t => t.Text == "NOTE" && t.PageIndex == 0);
        Assert.DoesNotContain(result.Texts, t => t.Text == "FAR" && t.PageIndex == 1);

        var path = Path.Combine(_folder.FullName, "layout.pdf");
        File.WriteAllBytes(path, result.Pdf);
        await using var document = await _engine.OpenAsync(path);
        Assert.Equal(2, document.PageCount);
        Assert.Equal(["Model", "A3 Plan"], (await document.GetBookmarksAsync()).Select(b => b.Title));

        // Measuring inside the viewport uses its 1:10 scale; on the rest of the paper, 1:1.
        var scales = await document.GetPageScalesAsync(1);
        Assert.Equal("1:10", Pdf.Measure.PdfPageScale.At(scales, new NormalizedPoint(0.5, 0.5))!.Ratio);
        Assert.Equal("1:1", Pdf.Measure.PdfPageScale.At(scales, new NormalizedPoint(0.02, 0.02))!.Ratio);

        // The model's left wall (x = 0) lands 100 mm left of the viewport centre: 110 mm on the paper, + 5 mm margin.
        var width = (int)(document.PageSizes[1].Width * 2);
        var height = (int)(document.PageSizes[1].Height * 2);
        var bitmap = await document.RenderPageAsync(1, width, height);
        var x = (int)(115 * 72 / 25.4 * 2);
        var y = height / 2;
        var dark = Enumerable.Range(x - 3, 7).Any(column => bitmap.Pixels[(y * bitmap.Stride) + (column * 4)] < 100);
        Assert.True(dark, "The model wall is not where the viewport should show it.");
    }

    [Fact]
    public void Reports_an_unreadable_file()
    {
        var path = Path.Combine(_folder.FullName, "broken.dwg");
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        Assert.Throws<PdfException>(() => CadToPdf.Convert(path));
    }

    /// <summary>
    /// A 200 × 100 mm outline on layer WALL (red) made of a block inserted twice (two halves), a circle,
    /// a text and a rotated text on DIM (layer off).
    /// </summary>
    private string CreateDrawing()
    {
        var document = new CadDocument();
        document.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Millimeters;
        var wall = new Layer("WALL") { Color = new Color(1) };
        var dim = new Layer("DIM") { Color = new Color(5), IsOn = false };
        document.Layers.Add(wall);
        document.Layers.Add(dim);

        // Half of the outline as a block: a 100 × 100 open rectangle.
        var half = new BlockRecord("HALF");
        half.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(100, 0, 0)));
        half.Entities.Add(new Line(new XYZ(100, 0, 0), new XYZ(100, 100, 0)));
        half.Entities.Add(new Line(new XYZ(100, 100, 0), new XYZ(0, 100, 0)));
        document.BlockRecords.Add(half);

        document.Entities.Add(new Insert(half) { InsertPoint = new XYZ(0, 0, 0), Layer = wall });
        document.Entities.Add(new Insert(half) { InsertPoint = new XYZ(200, 0, 0), XScale = -1, Layer = wall });
        document.Entities.Add(new Circle { Center = new XYZ(100, 50, 0), Radius = 25, Layer = wall });
        document.Entities.Add(new TextEntity { Value = "PANEL A", InsertPoint = new XYZ(20, 80, 0), Height = 5, Layer = wall });
        document.Entities.Add(new TextEntity { Value = "%%c50", InsertPoint = new XYZ(150, 30, 0), Height = 3.5, Rotation = Math.PI / 2, Layer = dim });

        var path = Path.Combine(_folder.FullName, "drawing.dxf");
        DxfWriter.Write(path, document);
        return path;
    }

    /// <summary>
    /// Model: a 2000 × 1000 mm room with "ROOM" in the middle, "NOTE" on layer NOTES and "FAR" far away.
    /// Layout "A3 Plan": a 420 × 297 border, "SHEET A3", and a 400 × 200 viewport centred at (210, 150) showing
    /// the room's centre at 1:10, with NOTES frozen.
    /// </summary>
    private string CreateDrawingWithLayout()
    {
        var document = new CadDocument();
        document.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Millimeters;
        var notes = new Layer("NOTES");
        document.Layers.Add(notes);
        foreach (var (a, b) in new[] { ((0.0, 0.0), (2000.0, 0.0)), ((2000.0, 0.0), (2000.0, 1000.0)), ((2000.0, 1000.0), (0.0, 1000.0)), ((0.0, 1000.0), (0.0, 0.0)) })
        {
            document.Entities.Add(new Line(new XYZ(a.Item1, a.Item2, 0), new XYZ(b.Item1, b.Item2, 0)));
        }

        document.Entities.Add(new TextEntity { Value = "ROOM", InsertPoint = new XYZ(950, 500, 0), Height = 50 });
        document.Entities.Add(new TextEntity { Value = "NOTE", InsertPoint = new XYZ(1200, 300, 0), Height = 30, Layer = notes });
        document.Entities.Add(new TextEntity { Value = "FAR", InsertPoint = new XYZ(20000, 500, 0), Height = 50 });

        var layout = new ACadSharp.Objects.Layout("A3 Plan");
        document.Layouts.Add(layout);
        var paper = layout.AssociatedBlock;
        foreach (var (a, b) in new[] { ((0.0, 0.0), (420.0, 0.0)), ((420.0, 0.0), (420.0, 297.0)), ((420.0, 297.0), (0.0, 297.0)), ((0.0, 297.0), (0.0, 0.0)) })
        {
            paper.Entities.Add(new Line(new XYZ(a.Item1, a.Item2, 0), new XYZ(b.Item1, b.Item2, 0)));
        }

        paper.Entities.Add(new TextEntity { Value = "SHEET A3", InsertPoint = new XYZ(330, 15, 0), Height = 5 });
        var viewport = new Viewport
        {
            Center = new XYZ(210, 150, 0),
            Width = 400,
            Height = 200,
            ViewCenter = new XY(1000, 500),
            ViewHeight = 2000,
        };
        viewport.FrozenLayers.Add(notes);
        paper.Entities.Add(viewport);

        var path = Path.Combine(_folder.FullName, "layout.dxf");
        DxfWriter.Write(path, document);
        return path;
    }
}
