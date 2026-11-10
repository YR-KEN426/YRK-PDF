using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.Core.Tests;

public sealed partial class SavedItemEditingTests
{
    public static TheoryData<string, double> SignatureCases => new()
    {
        { "vector", .3 }, { "vector", .5 }, { "vector", .8 },
        { "drawn", .3 }, { "drawn", .5 }, { "drawn", .8 },
        { "svg", .3 }, { "svg", .5 }, { "svg", .8 },
        { "image", .3 }, { "image", .5 }, { "image", .8 },
        { "bitmap-item", .3 }, { "bitmap-item", .5 }, { "bitmap-item", .8 },
    };

    internal static byte[] SignatureBlank() => Blank();

    // Wide intersecting strokes; all samples lie well inside ink, away from antialiasing.
    internal static SignatureArt CrossingSignature(string source, string folder)
    {
        PdfVectorArt art;
        if (source == "drawn")
        {
            var attributes = new DrawingAttributes { Color = Colors.Black, Width = 20, Height = 20 };
            art = VectorArtBuilder.FromStrokes(new StrokeCollection
            {
                new Stroke(new StylusPointCollection { new StylusPoint(10, 50), new StylusPoint(90, 50) }, attributes),
                new Stroke(new StylusPointCollection { new StylusPoint(50, 10), new StylusPoint(50, 90) }, attributes),
            })!;
        }
        else if (source == "svg")
        {
            var path = Path.Combine(folder, "cross.svg");
            File.WriteAllText(path, """
                <svg xmlns="http://www.w3.org/2000/svg">
                  <path d="M 0 40 L 100 40 L 100 60 L 0 60 Z"/>
                  <path d="M 40 0 L 60 0 L 60 100 L 40 100 Z"/>
                </svg>
                """);
            art = SvgImport.Load(path);
        }
        else
        {
            static PdfVectorPath Rectangle(double x, double y, double w, double h) => new(
                [PdfPathCommand.Move(x, y), PdfPathCommand.Line(x + w, y), PdfPathCommand.Line(x + w, y + h),
                    PdfPathCommand.Line(x, y + h), PdfPathCommand.Closing], new(0, 0, 0));
            art = new(100, 100, [Rectangle(0, 40, 100, 20), Rectangle(40, 0, 20, 100)]);
        }
        Assert.Equal(2, art.Paths.Count);
        Assert.All(art.Paths, p => Assert.Equal(255, p.Color.A));
        // Exercise the stored-signature route too, including its cached on-screen preview.
        var signature = SignatureArt.Deserialize(SignatureArt.FromVector(art).Serialize());
        return source is "image" or "bitmap-item" ? SignatureArt.FromImage(signature.Preview) : signature;
    }

    internal static PlacedItemViewModel CrossingItem(SignatureArt art, double opacity, bool bitmapOnly = false)
    {
        var item = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Signature, 400, 400,
            art.Preview, bitmapOnly ? null : art);
        item.Bounds = new(.25, .25, .5, .5);
        item.Opacity = opacity;
        return item;
    }

    [Theory]
    [MemberData(nameof(SignatureCases))]
    public async Task Signature_real_save_and_saved_item_opacity_keep_crossing_uniform(string source, double opacity)
    {
        await using var app = new AppTestHarness();
        var blank = app.PathFor("blank.pdf"); File.WriteAllBytes(blank, Blank());
        Assert.True(await app.OpenAsync(blank));
        var art = Sta.Run(() => CrossingSignature(source, app.Folder));
        Attach(app, CrossingItem(art, opacity, source == "bitmap-item"));
        var saved = app.PathFor("signature.pdf");
        Assert.True(await app.SaveToAsync(saved));
        Assert.True(await app.OpenAsync(saved));
        var item = Assert.Single(await Items(app));
        Assert.Equal(YrkMarkKind.Signature, item.Kind);
        Assert.Equal(PdfItemOpacityMode.Group, item.OpacityMode);
        Assert.Null(item.EditError);
        var leaves = await YrkMarkTests.ReadObjectsAsync(File.ReadAllBytes(saved));
        Assert.Equal(art.Vector is null ? 1 : 2, leaves.Count);
        Assert.All(leaves, leaf => Assert.Equal(art.Vector is null ? 3 : 2, leaf.Type));
        MeasureCrossing(await Render(app), opacity, $"pdf-{source}");

        // The step 7 UI operation must replace the group's alpha, including increasing it.
        await Select(app);
        foreach (var editedOpacity in new[] { .8, .3, .5 })
        {
            app.Model.ChangeItemOpacity(Frame(app), editedOpacity);
            await EditFinished(app);
            var edited = app.PathFor($"edited-{editedOpacity}.pdf");
            Assert.True(await app.SaveToAsync(edited));
            Assert.True(await app.OpenAsync(edited));
            Assert.InRange(Assert.Single(await Items(app)).Opacity, editedOpacity - .002, editedOpacity + .002);
            MeasureCrossing(await Render(app), editedOpacity, $"pdf-edited-{source}-from-{opacity:0.0}");
            await Select(app);
        }
    }

    [Theory]
    [MemberData(nameof(SignatureCases))]
    public async Task Signature_unmarked_save_uses_the_same_group(string source, double opacity)
    {
        await using var app = new AppTestHarness();
        var art = Sta.Run(() => CrossingSignature(source, app.Folder));
        await using var document = await app.Engine.OpenBytesAsync(Blank(), app.PathFor("blank.pdf"));
        var style = new PlacementStyle(Opacity: opacity);
        if (art.Vector is { } vector) await document.AddVectorAsync(0, vector, new(.25, .25, .5, .5), style: style);
        else await document.AddImageAsync(0, ImageConversion.ToPdfImage(art.Preview), new(.25, .25, .5, .5), style: style);
        var saved = app.PathFor("unmarked.pdf"); await document.SaveAsync(saved);
        await using var reopened = await app.Engine.OpenAsync(saved);
        Assert.All(await reopened.GetPageItemsAsync(0), i => Assert.False(i.HasYrkMark));
        MeasureCrossing(await reopened.RenderPageAsync(0, 400, 400), opacity, $"pdf-unmarked-{source}");
    }

    [Fact]
    public void Signature_real_WPF_template_and_ItemRenderer_apply_opacity_once()
    {
        Sta.Run(() =>
        {
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, typeof(MainWindow).Assembly);
            ResetApplication();
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/YrkEasyPdf;component/Themes/Styles.xaml", UriKind.Relative) });
            var recents = typeof(RecentFiles).GetField("s_items", BindingFlags.NonPublic | BindingFlags.Static)!;
            var oldRecents = recents.GetValue(null);
            recents.SetValue(null, new ObservableCollection<RecentFile>());
            var app = new AppTestHarness();
            MainWindow? window = null;
            try
            {
                window = new MainWindow(app.Model, () => throw new NotSupportedException());
                var template = (DataTemplate)window.Resources["PlacedItemTemplate"];
                foreach (var source in new[] { "vector", "drawn", "svg", "image", "bitmap-item" })
                {
                    var art = CrossingSignature(source, app.Folder);
                    var item = CrossingItem(art, 1, source == "bitmap-item");
                    var presenter = new ContentPresenter { Content = item, ContentTemplate = template };
                    // Host the production template on white paper at its actual page coordinates.
                    var canvas = new Canvas { Width = 400, Height = 400, Background = Brushes.White };
                    Canvas.SetLeft(presenter, 100); Canvas.SetTop(presenter, 100);
                    canvas.Children.Add(presenter);
                    window.Content = canvas;
                    presenter.ApplyTemplate();
                    foreach (var selected in new[] { false, true })
                    foreach (var opacity in new[] { .3, .5, .8 })
                    {
                        item.IsSelected = selected;
                        app.Model.ChangeItemOpacity(item, opacity);
                        window.Measure(new Size(1200, 800)); window.Arrange(new Rect(0, 0, 1200, 800));
                        canvas.Measure(new Size(400, 400)); canvas.Arrange(new Rect(0, 0, 400, 400));
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        canvas.UpdateLayout();
                        var image = Assert.Single(Descendants<Image>(presenter));
                        var wrapper = Assert.IsType<Grid>(VisualTreeHelper.GetParent(image));
                        Assert.Equal(opacity, wrapper.Opacity);
                        Assert.Same(item.Image, image.Source); Assert.Equal(1, image.Opacity);
                        var target = new RenderTargetBitmap(400, 400, 96, 96, PixelFormats.Pbgra32);
                        target.Render(canvas);
                        var pixels = new byte[400 * 400 * 4]; target.CopyPixels(pixels, 1600, 0);
                        MeasureCrossing(new(400, 400, 1600, pixels), opacity, $"screen-{source}-{(selected ? "selected" : "placing")}");

                        // ItemRenderer returns intrinsic ink; AddImageAsync applies placement opacity.
                        var rendered = ItemRenderer.Render(item);
                        int Alpha(double x, double y) => rendered.Pixels[((int)(y * rendered.Height) * rendered.Width + (int)(x * rendered.Width)) * 4 + 3];
                        Assert.Equal(255, Alpha(.5, .5)); Assert.Equal(255, Alpha(.25, .5));
                        Assert.Equal(ImageConversion.ToPdfImage(art.Preview).Pixels, rendered.Pixels);
                    }
                }
                return true;
            }
            finally
            {
                window?.Close(); Await(app.DisposeAsync().AsTask());
                recents.SetValue(null, oldRecents);
                application.Shutdown(); ResetApplication();
            }
        });
    }

    internal static void MeasureCrossing(PdfBitmap bitmap, double opacity, string route)
    {
        int[] Rgb(int x, int y)
        {
            var i = y * bitmap.Stride + x * 4;
            Assert.Equal(255, bitmap.Pixels[i + 3]);
            return [bitmap.Pixels[i + 2], bitmap.Pixels[i + 1], bitmap.Pixels[i]];
        }
        var cross = Rgb(200, 200); var single = Rgb(150, 200); var other = Rgb(200, 150);
        var line = $"{route} {opacity:P0}: crossing=({string.Join(",", cross)}) single=({string.Join(",", single)}) other=({string.Join(",", other)})";
        // Write before asserting so a mutation run also leaves its actual failing measurements.
        if (Environment.GetEnvironmentVariable("YRK_SHOT_DIR") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, $"opacity-{route}-{opacity * 100:0}.txt"), line + Environment.NewLine);
        }
        Assert.All(Enumerable.Range(0, 3), channel =>
        {
            Assert.True(Math.Abs(cross[channel] - single[channel]) <= 2, line);
            Assert.True(Math.Abs(cross[channel] - other[channel]) <= 2, line);
            Assert.InRange(single[channel], (int)(255 * (1 - opacity)) - 2, (int)(255 * (1 - opacity)) + 2);
        });
        Assert.Equal(new[] { 255, 255, 255 }, Rgb(110, 110));
    }
}

// RecoveryStore.Folder is process-wide: this existing nonparallel collection also protects
// the encrypted-recovery tests, and prevents overlap with RecoveryStoreTests.
[Collection("Temporary file security")]
public sealed class SignatureRecoveryOpacityTests
{
    [Theory]
    [MemberData(nameof(SavedItemEditingTests.SignatureCases), MemberType = typeof(SavedItemEditingTests))]
    public async Task Signature_recovery_uses_the_real_signature_writer(string source, double opacity)
    {
        var app = new AppTestHarness();
        var previousFolder = RecoveryStore.Folder;
        RecoveryStore.Folder = app.PathFor("recovery");
        try
        {
            var blank = app.PathFor("blank.pdf"); File.WriteAllBytes(blank, SavedItemEditingTests.SignatureBlank());
            Assert.True(await app.OpenAsync(blank));
            var art = Sta.Run(() => SavedItemEditingTests.CrossingSignature(source, app.Folder));
            var item = SavedItemEditingTests.CrossingItem(art, opacity, source == "bitmap-item");
            typeof(MainViewModel).GetMethod("AttachItem", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(app.Model, [item]);
            await app.Model.WriteRecoveryAsync();
            var path = Assert.Single(Directory.GetFiles(RecoveryStore.Folder, "*.pdf"));
            await using var recovered = await app.Engine.OpenAsync(path);
            SavedItemEditingTests.MeasureCrossing(await recovered.RenderPageAsync(0, 400, 400), opacity, $"pdf-recovery-{source}");
            var group = Assert.Single(await recovered.GetPageItemsAsync(0));
            Assert.Equal(YrkMarkKind.Signature, group.Kind);
            Assert.Equal(PdfItemOpacityMode.Group, group.OpacityMode);
            var leaves = await YrkMarkTests.ReadObjectsAsync(File.ReadAllBytes(path));
            Assert.Equal(art.Vector is null ? 1 : 2, leaves.Count);
            Assert.All(leaves, leaf => Assert.Equal(art.Vector is null ? 3 : 2, leaf.Type));
        }
        finally
        {
            try { await app.DisposeAsync(); }
            finally { RecoveryStore.Folder = previousFolder; }
        }
    }
}
