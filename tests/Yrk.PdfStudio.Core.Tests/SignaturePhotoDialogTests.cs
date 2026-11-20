using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.Views;
using Yrk.PdfStudio.Testing;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class SignaturePhotoDialogTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("th")]
    public void Good_bad_cancel_and_good_again_update_the_real_import_state(string language) => Sta.Run(() =>
    {
        using var app = new WpfTestApp(language);
        var window = new SignatureDialog(_ => Task.FromResult<SignatureArt?>(null));
        var directory = Path.Combine(Path.GetTempPath(), $"signature-dialog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var modes = (TabControl)window.FindName("Modes");
            var preview = (Image)window.FindName("ImportedPreview");
            var tips = (ScrollViewer)window.FindName("ImportTips");
            var failure = (TextBlock)window.FindName("ImportFailure");
            var use = (Button)window.FindName("UseButton");
            modes.SelectedIndex = 2;
            WpfTestApp.Layout(window);
            Assert.False(use.IsEnabled);
            Assert.Equal(Visibility.Visible, tips.Visibility);
            Assert.Equal(Visibility.Collapsed, failure.Visibility);
            var text = ((StackPanel)tips.Content).Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains(language == "th" ? "ถ่ายรูปลายเซ็นให้ได้ผลดี" : "Tips for a good signature photo", text);
            Assert.Equal(4, text.Count(t => t.StartsWith('•')));
            Assert.Equal(language == "th"
                ? "ลบพื้นได้ไม่ดี ลองถ่ายใหม่ในที่สว่างและไม่มีเงา"
                : "Couldn't remove the paper well. Try again in good light with no shadow.", failure.Text);

            var good = Path.Combine(directory, "good.png");
            var bad = Path.Combine(directory, "bad.png");
            SaveExample(good, withInk: true);
            SaveExample(bad, withInk: false);
            WpfTestApp.Await(window.ImportSignatureFileAsync(good));
            Assert.True(use.IsEnabled);
            Assert.NotNull(preview.Source);
            Assert.Equal(Visibility.Collapsed, tips.Visibility);
            Assert.Equal(Visibility.Collapsed, failure.Visibility);
            var firstPreview = preview.Source;
            WpfTestApp.Await(window.ImportSignatureFileAsync(null));
            Assert.Same(firstPreview, preview.Source);
            Assert.True(use.IsEnabled);
            Assert.Equal(Visibility.Collapsed, failure.Visibility);

            WpfTestApp.Await(window.ImportSignatureFileAsync(bad));
            Assert.False(use.IsEnabled);
            Assert.Null(preview.Source);
            Assert.Null(typeof(SignatureDialog).GetField("_imported", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.Null(typeof(SignatureDialog).GetMethod("CreateSignature", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null));
            Assert.Equal(Visibility.Visible, tips.Visibility);
            Assert.Equal(Visibility.Visible, failure.Visibility);
            WpfTestApp.Await(window.ImportSignatureFileAsync(null));
            Assert.False(use.IsEnabled);
            Assert.Null(preview.Source);
            Assert.Equal(Visibility.Visible, failure.Visibility);

            WpfTestApp.Await(window.ImportSignatureFileAsync(good));
            Assert.True(use.IsEnabled);
            Assert.NotNull(preview.Source);
            Assert.Equal(Visibility.Collapsed, tips.Visibility);
            Assert.Equal(Visibility.Collapsed, failure.Visibility);
            Assert.Empty(app.Errors);
        }
        finally
        {
            window.Close();
            Directory.Delete(directory, recursive: true);
        }
        return true;
    });

    private static void SaveExample(string path, bool withInk)
    {
        const int width = 300, height = 180;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var value = withInk && x is >= 65 and <= 235 && y is >= 88 and <= 90 ? (byte)20 : (byte)235;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
