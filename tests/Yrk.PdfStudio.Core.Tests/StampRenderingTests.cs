using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.App.Views;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Text;
using Yrk.PdfStudio.Testing;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class StampRenderingTests
{
    private static readonly DateTime Day = new(2026, 10, 29);
    public static IEnumerable<object[]> Designs => Enum.GetValues<StampLanguage>().SelectMany(language =>
        Enumerable.Range(0, 9).SelectMany(preset => Enum.GetValues<StampShape>().Select(shape => new object[] { language, preset, shape })));

    [Theory]
    [MemberData(nameof(Designs))]
    public void All_nine_presets_in_five_shapes_and_three_languages_keep_glyphs_inside_frame(StampLanguage language, int preset, StampShape shape)
    {
        Sta.Run(() =>
        {
            var design = RubberStamp.Presets(language)[preset] with { Shape = shape };
            foreach (var sample in new[] { design, design with { Detail = (design.Detail ?? "") + "\nผู้ซื้อ ผู้รับ ผู้อนุมัติ ฐานข้อมูล\nบริษัททดสอบรายละเอียดที่ยาวมาก ABC International Co., Ltd.", Date = StampDateStyle.ThaiLong } })
            {
                var stamp = RubberStamp.Render(sample, Day, 1);
                Assert.NotNull(stamp.Frame);
                Assert.Equal(stamp.Image.PixelWidth, stamp.Frame!.PixelWidth);
                if (shape == StampShape.Seal) Assert.Equal(stamp.Size.Width, stamp.Size.Height);
                foreach (var line in stamp.Lines!)
                {
                    var text = TextRendering.Create(line.Text, line.FontSize, Brushes.Black, line.Bold ? FontWeights.Bold : FontWeights.Normal);
                    var bounds = text.BuildGeometry(line.TopLeft).Bounds;
                    foreach (var point in new[] { bounds.TopLeft, bounds.TopRight, bounds.BottomLeft, bounds.BottomRight })
                    {
                        var w = stamp.Size.Width; var h = stamp.Size.Height;
                        Assert.InRange(point.X, 2, w - 2); Assert.InRange(point.Y, 2, h - 2);
                        if (shape is StampShape.Oval or StampShape.Seal)
                        {
                            var nx = (point.X - w / 2) / (w / 2 - 5);
                            var ny = (point.Y - h / 2) / (h / 2 - 5);
                            Assert.True(nx * nx + ny * ny < 1, $"{shape}: {line.Text} at {point}");
                        }
                        if (shape == StampShape.Ribbon) Assert.InRange(point.X, 15, w - 15);
                    }
                }
            }
            return true;
        });
    }

    [Theory]
    [InlineData("th")]
    [InlineData("en")]
    public void Dialog_has_one_date_input_named_styles_color_rings_selected_border_and_matching_preview(string language)
    {
        var folder = Directory.CreateTempSubdirectory("yrk-stamp-dialog-");
        try
        {
            Sta.Run(() =>
            {
                using var app = new WpfTestApp(language);
                var dialog = new StampDialog(StampSettings.Load(Path.Combine(folder.FullName, "stamps.json")));
                try
                {
                    dialog.ShowInTaskbar = false; dialog.Left = -32000; dialog.Show(); WpfTestApp.Layout(dialog);
                    Assert.Single(WpfTestApp.Descendants(dialog).OfType<DatePicker>());
                    var date = (DatePicker)dialog.FindName("DateInput"); date.SelectedDate = Day;
                    var styles = (ComboBox)dialog.FindName("DateChoice");
                    Assert.Equal(5, styles.Items.Count);
                    Assert.All(styles.Items.Cast<string>(), text => Assert.DoesNotContain("2026", text, StringComparison.Ordinal));
                    var shapes = (ComboBox)dialog.FindName("ShapeChoice"); Assert.Equal(5, shapes.Items.Count);
                    foreach (var name in new[] { "RedInk", "BlueInk", "GreenInk", "BlackInk" })
                        Assert.Same(Application.Current.Resources["ColorRing"], ((RadioButton)dialog.FindName(name)).Style);
                    var presets = (ListBox)dialog.FindName("PresetList"); presets.SelectedIndex = 0; WpfTestApp.Layout(dialog);
                    var item = (ListBoxItem)presets.ItemContainerGenerator.ContainerFromIndex(0);
                    var border = WpfTestApp.Descendants(item).OfType<Border>().Single(b => b.Width == 150);
                    Assert.Equal(new Thickness(2), border.BorderThickness);
                    Assert.Same(Application.Current.Resources["AccentBrush"], border.BorderBrush);
                    foreach (var shape in new[] { StampShape.Ribbon, StampShape.Seal })
                    {
                        shapes.SelectedIndex = (int)shape; WpfTestApp.Layout(dialog);
                        var design = (StampDesign)typeof(StampDialog).GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
                        Assert.Equal(shape, design.Shape);
                        var preview = (BitmapSource)((Image)dialog.FindName("Preview")).Source;
                        Assert.Equal(Pixels(RubberStamp.Render(design, Day, 2).Image), Pixels(preview));
                    }
                    ExportReview(dialog, language);
                    Assert.Empty(app.Errors);
                }
                finally { dialog.Close(); }
                return true;
            });
        }
        finally { folder.Delete(true); }
    }

    [Theory]
    [InlineData(StampShape.Ribbon)]
    [InlineData(StampShape.Seal)]
    public async Task Saved_new_stamp_is_searchable_and_text_pixels_match_dialog_preview(StampShape shape)
    {
        var folder = Directory.CreateTempSubdirectory("yrk-stamp-pdf-");
        using var engine = new PdfiumEngine();
        try
        {
            var design = new StampDesign("อนุมัติ APPROVED", "ผู้ซื้อ ผู้รับ ฐานข้อมูล\nABC International Co., Ltd.", StampInk.Blue, shape, StampDateStyle.ThaiLong);
            var stamp = Sta.Run(() => RubberStamp.Render(design, Day, 2));
            var source = Path.Combine(folder.FullName, "source.pdf");
            var framed = Path.Combine(folder.FullName, "frame.pdf");
            var saved = Path.Combine(folder.FullName, "stamp.pdf");
            File.WriteAllBytes(source, SignatureFormFillTests.BlankDocument());
            double pageWidth, pageHeight;
            const double left = 20, top = 20;
            await using (var document = await engine.OpenAsync(source))
            {
                pageWidth = document.PageSizes[0].Width; pageHeight = document.PageSizes[0].Height;
                await document.AddImageAsync(0, ImageConversion.ToPdfImage(stamp.Frame!), new(left / pageWidth, top / pageHeight, stamp.Size.Width / pageWidth, stamp.Size.Height / pageHeight));
                await document.SaveAsync(framed);
            }
            var blocks = Sta.Run(() =>
            {
                var lines = new List<(int PageIndex, NormalizedPoint TopLeft, PdfColor Color, TextShaper.ShapedLine Line)>();
                // Exercise the application's actual placement code, with its shared StampLine positions.
                typeof(MainViewModel).GetMethod("AddStampWords", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [lines, stamp, 0, new NormalizedRect(left / pageWidth, top / pageHeight, stamp.Size.Width / pageWidth, stamp.Size.Height / pageHeight), pageWidth, pageHeight]);
                return TextShaper.ToBlocks(lines);
            });
            File.WriteAllBytes(saved, PdfTextWriter.AddText(File.ReadAllBytes(framed), blocks));
            await using var reopened = await engine.OpenAsync(saved);
            Assert.NotEmpty(await reopened.FindTextAsync(0, "APPROVED", PdfSearchOptions.None));
            Assert.NotEmpty(await reopened.FindTextAsync(0, "อนุมัติ", PdfSearchOptions.None));
            Assert.NotEmpty(await reopened.FindTextAsync(0, "ABC International", PdfSearchOptions.None));
            var bitmap = await reopened.RenderPageAsync(0, (int)(pageWidth * 2), (int)(pageHeight * 2));
            var preview = Pixels(stamp.Image);
            foreach (var line in stamp.Lines!)
            {
                var rect = Sta.Run(() => TextRendering.Create(line.Text, line.FontSize, Brushes.Black, FontWeights.Bold).BuildGeometry(line.TopLeft).Bounds);
                var expected = new List<(int X, int Y)>(); var actual = new List<(int X, int Y)>();
                for (var y = (int)Math.Floor(rect.Top * 2) - 2; y <= Math.Ceiling(rect.Bottom * 2) + 2; y++)
                    for (var x = (int)Math.Floor(rect.Left * 2) - 2; x <= Math.Ceiling(rect.Right * 2) + 2; x++)
                    {
                        var i = (y * stamp.Image.PixelWidth + x) * 4;
                        if (preview[i + 3] > 100) expected.Add((x, y));
                        var j = ((y + (int)(top * 2)) * bitmap.Stride) + (x + (int)(left * 2)) * 4;
                        if (bitmap.Pixels[j + 2] < 160 && bitmap.Pixels[j] > 70) actual.Add((x, y));
                    }
                Assert.NotEmpty(expected); Assert.NotEmpty(actual);
                static double Coverage(List<(int X, int Y)> a, List<(int X, int Y)> b) =>
                    a.Count(p => b.Any(q => Math.Abs(p.X - q.X) <= 3 && Math.Abs(p.Y - q.Y) <= 3)) / (double)a.Count;
                Assert.True(Coverage(expected, actual) > .95 && Coverage(actual, expected) > .95, $"{shape}: {line.Text}: preview/PDF glyph positions differ");
            }
        }
        finally { folder.Delete(true); }
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4]; source.CopyPixels(pixels, source.PixelWidth * 4, 0); return pixels;
    }

    private static void ExportReview(Window dialog, string language)
    {
        var output = Environment.GetEnvironmentVariable("YRK_STAMP_REVIEW_DIR");
        if (string.IsNullOrEmpty(output)) return;
        // Review artifacts are opt-in and must live in the caller's temporary directory.
        var root = Path.GetFullPath(output);
        var temp = Path.GetFullPath(Environment.GetEnvironmentVariable("YRK_STAMP_REVIEW_TEMP_ROOT") ?? Path.GetTempPath());
        if (!root.StartsWith(Path.TrimEndingDirectorySeparator(temp) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Stamp review output must be temporary.");
        Directory.CreateDirectory(root);
        var image = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(dialog);
        Save(image, Path.Combine(root, "stamp-dialog-" + language + ".png"));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1500, 1000));
            foreach (var shape in Enum.GetValues<StampShape>())
            {
                var x = (int)shape * 300;
                dc.DrawText(TextRendering.Create(shape.ToString(), 16, Brushes.Black), new Point(x + 10, 10));
                var presets = RubberStamp.Presets(language == "th" ? StampLanguage.Both : StampLanguage.English);
                for (var i = 0; i < presets.Count; i++)
                {
                    var stamp = RubberStamp.Render(presets[i] with { Shape = shape }, Day, 2);
                    var scale = Math.Min(270 / stamp.Size.Width, 85 / stamp.Size.Height);
                    dc.DrawImage(stamp.Image, new Rect(x + 10, 45 + i * 103, stamp.Size.Width * scale, stamp.Size.Height * scale));
                }
            }
        }
        Save(ImageConversion.Render(visual, 1500, 1000), Path.Combine(root, "stamp-shapes-" + language + ".png"));
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
