using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ImageInputLoaderTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-images-");

    public void Dispose() => _folder.Delete(recursive: true);

    [Fact]
    public void Embeds_upright_jpeg_unchanged()
    {
        var path = WriteJpeg("upright.jpg", 400, 300, orientation: null);

        var input = Assert.IsType<PdfJpegInput>(ImageInputLoader.Load(path));

        Assert.Equal(File.ReadAllBytes(path), input.Data);
        Assert.Equal((400, 300), (input.PixelWidth, input.PixelHeight));
    }

    [Fact]
    public void Turns_phone_photos_upright_using_exif_orientation()
    {
        // Orientation 6: the camera was held upright, the sensor image is stored sideways.
        var path = WriteJpeg("phone.jpg", 400, 300, orientation: 6);

        var input = Assert.IsType<PdfJpegInput>(ImageInputLoader.Load(path));

        Assert.Equal((300, 400), (input.PixelWidth, input.PixelHeight));
        Assert.NotEqual(File.ReadAllBytes(path), input.Data);
    }

    [Fact]
    public void Stores_png_losslessly()
    {
        var path = Path.Combine(_folder.FullName, "screen.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Solid(120, 80)));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        var input = Assert.IsType<PdfBitmapInput>(ImageInputLoader.Load(path));

        Assert.Equal((120, 80), (input.Image.Width, input.Image.Height));
    }

    [Fact]
    public void Rejects_files_that_are_not_images()
    {
        var path = Path.Combine(_folder.FullName, "notes.jpg");
        File.WriteAllText(path, "not an image");

        Assert.Throws<NotSupportedException>(() => ImageInputLoader.Load(path));
    }

    private string WriteJpeg(string name, int width, int height, ushort? orientation)
    {
        var metadata = new BitmapMetadata("jpg");
        if (orientation is { } value)
        {
            metadata.SetQuery("/app1/ifd/{ushort=274}", value);
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(Solid(width, height), null, metadata, null));
        var path = Path.Combine(_folder.FullName, name);
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    private static BitmapSource Solid(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        Array.Fill(pixels, (byte)90);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3);
        bitmap.Freeze();
        return bitmap;
    }
}
