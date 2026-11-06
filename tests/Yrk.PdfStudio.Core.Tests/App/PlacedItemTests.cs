using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class PlacedItemTests
{
    [Fact]
    public async Task Rotation_and_opacity_gestures_are_single_undo_steps_and_redo_restores_them()
    {
        await using var app = new AppTestHarness();
        var item = ImageItem();
        var bounds = item.Bounds;
        Assert.Equal(0, item.Rotation);
        Assert.Equal(1, item.Opacity);
        app.Model.BeginItemDrag(item);
        app.Model.RotateItem(item, 22, snap: false);
        Assert.Equal(22, item.Rotation);
        app.Model.RotateItem(item, 37, snap: true);
        Assert.Equal(30, item.Rotation);
        app.Model.EndItemDrag(item);
        app.Model.BeginItemDrag(item);
        item.Opacity = 0.8;
        item.Opacity = 0.4;
        app.Model.EndItemDrag(item);
        app.Model.UndoCommand.Execute(null);
        Assert.Equal(1, item.Opacity);
        Assert.Equal(30, item.Rotation);
        app.Model.UndoCommand.Execute(null);
        Assert.Equal(0, item.Rotation);
        Assert.False(app.Model.UndoCommand.CanExecute(null));
        app.Model.RedoCommand.Execute(null);
        app.Model.RedoCommand.Execute(null);
        Assert.Equal(30, item.Rotation);
        Assert.Equal(0.4, item.Opacity);
        Assert.Equal(bounds, item.Bounds);
        app.Model.ChangeItemOpacity(item, 0.7);
        app.Model.UndoCommand.Execute(null);
        Assert.Equal(0.4, item.Opacity);
        app.Model.RedoCommand.Execute(null);
        Assert.Equal(0.7, item.Opacity);
    }

    [Fact]
    public async Task Four_quarter_turns_restore_the_exact_original_placement()
    {
        await using var app = new AppTestHarness();
        var item = ImageItem();
        var before = item.Bounds;
        for (var i = 0; i < 4; i++) app.Model.RotateItem90(item);
        Assert.Equal(0, item.Rotation);
        Assert.Equal(before, item.Bounds);
        Assert.Equal(1, item.Opacity);
        app.Model.UndoCommand.Execute(null);
        Assert.Equal(270, item.Rotation);
        app.Model.RedoCommand.Execute(null);
        Assert.Equal(0, item.Rotation);
        Assert.Equal(before, item.Bounds);
    }

    [Theory]
    [InlineData(30, 20)]
    [InlineData(90, 20)]
    [InlineData(225, -20)]
    [InlineData(315, -1000)]
    public async Task Resizing_a_rotated_item_keeps_the_opposite_screen_corner_and_aspect_ratio(double angle, double localDelta)
    {
        await using var app = new AppTestHarness();
        var item = ImageItem();
        item.Bounds = item.ToBounds(8, 10, 100, 50); // Also exercise an anchor near the page edge.
        item.Rotation = angle;
        var before = item.Bounds;
        var corner = Corner(item);
        app.Model.BeginItemDrag(item);
        var radians = angle * Math.PI / 180;
        app.Model.ResizeItem(item, Math.Cos(radians) * localDelta, Math.Sin(radians) * localDelta);
        app.Model.EndItemDrag(item);
        Assert.Equal(corner.X, Corner(item).X, 8);
        Assert.Equal(corner.Y, Corner(item).Y, 8);
        Assert.Equal(Math.Max(16, 100 + localDelta), item.Width, 8);
        Assert.Equal(2, item.Width / item.Height, 8);
        Assert.True(item.Width >= PlacedItemViewModel.MinimumSize && item.Height >= PlacedItemViewModel.MinimumSize);
        var resized = item.Bounds;
        app.Model.UndoCommand.Execute(null);
        Assert.Equal(before, item.Bounds);
        app.Model.RedoCommand.Execute(null);
        Assert.Equal(resized, item.Bounds);
    }

    [Theory]
    [InlineData("picture", 0, 90)]
    [InlineData("picture", 90, 30)]
    [InlineData("image", 0, 30)]
    [InlineData("vector", 90, 30)]
    [InlineData("pdf", 0, 90)]
    [InlineData("pdf", 90, 30)]
    public async Task Saved_art_matches_the_display_bounds_direction_and_opacity(string kind, int pageRotation, double rotation)
    {
        await using var app = new AppTestHarness();
        var source = BlankPdf(app.PathFor("source.pdf"), pageRotation);
        Assert.True(await app.OpenAsync(source));
        var image = TwoColourImage();
        SignatureArt? art = kind switch
        {
            "image" => SignatureArt.FromImage(image),
            "vector" => SignatureArt.FromVector(TwoColourVector()),
            "pdf" => SignatureArt.FromPdf(File.ReadAllBytes(BlankPdf(app.PathFor("art.pdf"), content:
                "1 0 0 rg 0 0 200 300 re f 0 0 1 rg 200 0 200 300 re f")), new NormalizedRect(0, 0, 1, 1), image),
            _ => null,
        };
        var size = app.Model.Pages[0];
        var item = PlacedItemViewModel.CreateImage(0, art is null ? PlacedItemKind.Picture : PlacedItemKind.Signature,
            size.WidthPoints, size.HeightPoints, art?.Preview ?? image, art);
        item.Bounds = item.ToBounds(80, 100, 120, 60);
        item.Rotation = rotation;
        item.Opacity = 0.5;
        Assert.Equal(kind != "pdf", item.CanAdjustOpacity);
        Assert.Equal(kind == "pdf" ? 1 : 0.5, item.Opacity);
        Attach(app, item);
        var saved = app.PathFor("saved.pdf");
        Assert.True(await app.SaveToAsync(saved));
        Assert.Empty(app.Dialogs.Named("ShowError"));
        await using var document = await app.Engine.OpenAsync(saved);
        var graphics = await document.GetPageGraphicsAsync(0);
        var left = graphics.Graphics.Min(g => g.X);
        var top = graphics.Graphics.Min(g => g.Y);
        var right = graphics.Graphics.Max(g => g.X + g.Width);
        var bottom = graphics.Graphics.Max(g => g.Y + g.Height);
        var radians = rotation * Math.PI / 180;
        var expectedWidth = item.Width * Math.Abs(Math.Cos(radians)) + item.Height * Math.Abs(Math.Sin(radians));
        var expectedHeight = item.Width * Math.Abs(Math.Sin(radians)) + item.Height * Math.Abs(Math.Cos(radians));
        Assert.Equal(expectedWidth, right - left, 1);
        Assert.Equal(expectedHeight, bottom - top, 1);
        Assert.Equal(item.Left + item.Width / 2, (left + right) / 2, 1);
        Assert.Equal(item.Top + item.Height / 2, (top + bottom) / 2, 1);
        var rendered = await document.RenderPageAsync(0, (int)size.WidthPoints * 2, (int)size.HeightPoints * 2);
        var pale = kind == "pdf" ? 0 : 127;
        CheckPixel(rendered, At(item, new Point(item.Left + 30, item.Top + 30)), 255, pale, pale);
        CheckPixel(rendered, At(item, new Point(item.Left + 90, item.Top + 30)), pale, pale, 255);
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(90, 90)]
    [InlineData(90, 30)]
    public async Task Stamp_real_text_rotates_with_the_frame_about_the_same_centre(int pageRotation, double rotation)
    {
        var stamp = Sta.Run(() => StampRenderer.RenderCertifiedCopy(new CertifiedCopyOptions("ROTATE", false, null)));
        var baseline = await SaveStampAsync(stamp, pageRotation, 0, 1);
        var rotated = await SaveStampAsync(stamp, pageRotation, rotation, 0.5);
        var angle = rotation * Math.PI / 180;
        const double centreX = 180, centreY = 140;
        int frameSamples = 0, wordSamples = 0, matchingFrame = 0, matchingWords = 0;
        // Compare solid interior ink against independently rendered, unrotated real text.
        // This detects the wrong sign, rotation about each line's origin, and missing text opacity.
        for (var y = 202; y < 358; y += 2)
        for (var x = 202; x < 518; x += 2)
        {
            var pixel = Pixel(baseline, x, y);
            if (pixel.B > 180 || pixel.R > 100) continue;
            if (Enumerable.Range(-1, 3).Any(d => Pixel(baseline, x + d, y).R > 100 || Pixel(baseline, x, y + d).R > 100)) continue;
            var dx = (x + 0.5) / 2 - centreX;
            var dy = (y + 0.5) / 2 - centreY;
            var rx = (int)((centreX + Math.Cos(angle) * dx - Math.Sin(angle) * dy) * 2);
            var ry = (int)((centreY + Math.Sin(angle) * dx + Math.Cos(angle) * dy) * 2);
            var actual = Pixel(rotated, rx, ry);
            var matches = Math.Abs(actual.R - (pixel.R + 255) / 2) <= 20
                && Math.Abs(actual.B - (pixel.B + 255) / 2) <= 20;
            if (x < 212 || x > 508 || y < 212 || y > 348)
            {
                frameSamples++;
                if (matches) matchingFrame++;
            }
            else
            {
                wordSamples++;
                if (matches) matchingWords++;
            }
        }
        Assert.True(frameSamples > 20, $"Only {frameSamples} frame pixels checked.");
        Assert.True(wordSamples > 20, $"Only {wordSamples} text pixels checked.");
        // Rotated raster interpolation and glyph hinting may change boundary pixels.
        Assert.True(matchingFrame >= frameSamples * 0.90, $"Frame: {matchingFrame}/{frameSamples}");
        Assert.True(matchingWords >= wordSamples * 0.90, $"Text: {matchingWords}/{wordSamples}");
    }

    [Theory]
    [InlineData("picture")]
    [InlineData("image")]
    [InlineData("vector")]
    [InlineData("pdf")]
    [InlineData("stamp")]
    public async Task Default_placement_passes_no_style_to_core(string kind)
    {
        await using var app = new AppTestHarness();
        Assert.True(await app.OpenAsync(BlankPdf(app.PathFor("source.pdf"))));
        var image = TwoColourImage();
        var art = kind switch
        {
            "image" => SignatureArt.FromImage(image),
            "vector" => SignatureArt.FromVector(TwoColourVector()),
            "pdf" => SignatureArt.FromPdf(File.ReadAllBytes(app.PathFor("source.pdf")), new NormalizedRect(0, 0, 1, 1), image),
            _ => null,
        };
        var stamp = kind == "stamp" ? Sta.Run(() => StampRenderer.RenderCertifiedCopy(new CertifiedCopyOptions(null, false, null))) : null;
        var item = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Signature, 400, 300, image, art, stamp);
        item.Bounds = item.ToBounds(80, 100, 120, 60);
        Attach(app, item);
        var calls = new List<object?[]>();
        var document = DispatchProxy.Create<IPdfDocument, SignatureSecurityTests.TestProxy>();
        ((SignatureSecurityTests.TestProxy)(object)document).Handle = (method, args) =>
        {
            Assert.Contains(method.Name, new[] { "AddImageAsync", "AddVectorAsync", "AddPdfPageAsync" });
            calls.Add(args!);
            return Task.CompletedTask;
        };
        await (Task)typeof(MainViewModel).GetMethod("CommitOverlaysAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app.Model, [document, kind == "stamp"])!;
        Assert.Null(Assert.Single(calls)[^1]);
    }

    private static async Task<PdfBitmap> SaveStampAsync(RenderedStamp stamp, int pageRotation, double rotation, double opacity)
    {
        await using var app = new AppTestHarness();
        Assert.True(await app.OpenAsync(BlankPdf(app.PathFor("source.pdf"), pageRotation)));
        var page = app.Model.Pages[0];
        var item = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Stamp, page.WidthPoints, page.HeightPoints, stamp.Image, stamp: stamp);
        item.Bounds = item.ToBounds(100, 100, 160, 80);
        item.Rotation = rotation;
        item.Opacity = opacity;
        Attach(app, item);
        var path = app.PathFor("saved.pdf");
        Assert.True(await app.SaveToAsync(path), string.Join("; ", app.Dialogs.Named("ShowError").Select(c => c.Arguments[0])));
        await using var document = await app.Engine.OpenAsync(path);
        Assert.Contains("ROTATE", await document.GetPageTextAsync(0), StringComparison.Ordinal);
        return await document.RenderPageAsync(0, (int)page.WidthPoints * 2, (int)page.HeightPoints * 2);
    }

    private static void Attach(AppTestHarness app, PlacedItemViewModel item) =>
        typeof(MainViewModel).GetMethod("AttachItem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app.Model, [item]);

    private static PlacedItemViewModel ImageItem()
    {
        var item = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Picture, 400, 300, TwoColourImage());
        item.Bounds = item.ToBounds(100, 100, 100, 50);
        return item;
    }

    private static BitmapSource TwoColourImage()
    {
        var image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, 8);
        image.Freeze();
        return image;
    }

    private static PdfVectorArt TwoColourVector() => new(2, 1,
    [
        new PdfVectorPath([PdfPathCommand.Move(0, 0), PdfPathCommand.Line(1, 0), PdfPathCommand.Line(1, 1), PdfPathCommand.Line(0, 1), PdfPathCommand.Closing], new PdfColor(255, 0, 0)),
        new PdfVectorPath([PdfPathCommand.Move(1, 0), PdfPathCommand.Line(2, 0), PdfPathCommand.Line(2, 1), PdfPathCommand.Line(1, 1), PdfPathCommand.Closing], new PdfColor(0, 0, 255)),
    ]);

    private static Point Corner(PlacedItemViewModel item) => At(item, new Point(item.Left, item.Top));

    private static Point At(PlacedItemViewModel item, Point point)
    {
        var centre = new Point(item.Left + item.Width / 2, item.Top + item.Height / 2);
        return new RotateTransform(item.Rotation, centre.X, centre.Y).Transform(point);
    }

    private static (int R, int G, int B) Pixel(PdfBitmap bitmap, int x, int y)
    {
        var offset = y * bitmap.Stride + x * 4;
        return (bitmap.Pixels[offset + 2], bitmap.Pixels[offset + 1], bitmap.Pixels[offset]);
    }

    private static void CheckPixel(PdfBitmap bitmap, Point point, int r, int g, int b)
    {
        var actual = Pixel(bitmap, (int)(point.X * 2), (int)(point.Y * 2));
        Assert.InRange(actual.R, Math.Max(0, r - 2), Math.Min(255, r + 2));
        Assert.InRange(actual.G, Math.Max(0, g - 2), Math.Min(255, g + 2));
        Assert.InRange(actual.B, Math.Max(0, b - 2), Math.Min(255, b + 2));
    }

    private static string BlankPdf(string path, int rotation = 0, string content = "")
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Rotate {rotation} /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        using var output = new MemoryStream();
        void Write(string text) => output.Write(Encoding.Latin1.GetBytes(text));
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(output.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = output.Position;
        Write("xref\n0 5\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, output.ToArray());
        return path;
    }
}
