using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Layers;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class LayerTests : IDisposable
{
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-layers-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task Lists_layers_with_headings_and_default_states()
    {
        var path = TestPdfFactory.CreateLayeredPdf(Path.Combine(_folder.FullName, "layers.pdf"));
        await using var document = await _engine.OpenAsync(path);

        var layers = await document.GetLayersAsync();

        Assert.Equal(["Model", "WALL", "DIM"], layers.Select(l => l.Name));
        Assert.True(layers[0].IsHeading);
        Assert.Equal(1, layers[1].Depth);
        Assert.True(layers[1].IsVisibleByDefault);
        Assert.True(layers[1].IsLocked);
        Assert.False(layers[2].IsVisibleByDefault);
    }

    [Fact]
    public async Task Hides_and_shows_layers_when_rendering()
    {
        var path = TestPdfFactory.CreateLayeredPdf(Path.Combine(_folder.FullName, "layers.pdf"));
        await using var document = await _engine.OpenAsync(path);
        var layers = await document.GetLayersAsync();
        var wall = layers.Single(l => l.Name == "WALL").Id;

        // Default: WALL (red, left) shown, DIM (blue, right) hidden.
        var normal = await document.RenderPageAsync(0, 595, 842);
        Assert.Equal(Red, Pixel(normal, 150, 192));
        Assert.Equal(White, Pixel(normal, 350, 192));

        // Hiding WALL and showing DIM.
        await document.SetHiddenLayersAsync([wall]);
        var swapped = await document.RenderPageAsync(0, 595, 842);
        Assert.Equal(White, Pixel(swapped, 150, 192));
        Assert.Equal(Blue, Pixel(swapped, 350, 192));

        // Changes to the document show up in the layer view too.
        await document.AddAnnotationAsync(0, new PdfShapeSpec(PdfAnnotationKind.Square, new NormalizedRect(0.1, 0.5, 0.3, 0.2), 4, new PdfColor(0, 160, 0)));
        var withComment = await document.RenderPageAsync(0, 595, 842);
        Assert.Equal(Blue, Pixel(withComment, 350, 192));
        Assert.Equal((0, 160, 0), Pixel(withComment, 60, 421));

        // Showing everything, including DIM, which is off by default.
        await document.SetHiddenLayersAsync([]);
        var all = await document.RenderPageAsync(0, 595, 842);
        Assert.Equal(Red, Pixel(all, 150, 192));
        Assert.Equal(Blue, Pixel(all, 350, 192));

        // Back to the document's own default.
        await document.SetHiddenLayersAsync(null);
        var initial = await document.RenderPageAsync(0, 595, 842);
        Assert.Equal(Red, Pixel(initial, 150, 192));
        Assert.Equal(White, Pixel(initial, 350, 192));
    }

    [Fact]
    public void Saves_layer_visibility_as_the_default()
    {
        var path = TestPdfFactory.CreateLayeredPdf(Path.Combine(_folder.FullName, "layers.pdf"));
        var bytes = File.ReadAllBytes(path);
        var layers = PdfLayerReader.Read(bytes);
        var wall = layers.Single(l => l.Name == "WALL").Id;

        var changed = PdfLayerReader.Read(PdfLayerReader.WithVisibility(bytes, new HashSet<string> { wall }));

        Assert.False(changed.Single(l => l.Name == "WALL").IsVisibleByDefault);
        Assert.True(changed.Single(l => l.Name == "DIM").IsVisibleByDefault);
        Assert.Equal(["Model", "WALL", "DIM"], changed.Select(l => l.Name));
    }

    [Fact]
    public async Task Has_no_layers_in_an_ordinary_pdf()
    {
        await using var document = await _engine.OpenAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf"));
        Assert.Empty(await document.GetLayersAsync());
    }

    private static readonly (int, int, int) Red = (255, 0, 0);
    private static readonly (int, int, int) Blue = (0, 0, 255);
    private static readonly (int, int, int) White = (255, 255, 255);

    private static (int R, int G, int B) Pixel(PdfBitmap bitmap, int x, int y)
    {
        var i = (y * bitmap.Stride) + (x * 4);
        return (bitmap.Pixels[i + 2], bitmap.Pixels[i + 1], bitmap.Pixels[i]);
    }
}
