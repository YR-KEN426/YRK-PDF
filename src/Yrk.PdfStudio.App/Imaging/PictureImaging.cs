using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>Pictures added to a page: read upright from a file or the clipboard, and sized to fit it.</summary>
internal static class PictureImaging
{
    /// <summary>Larger pictures are scaled down: a phone photo would otherwise add many megabytes to the file.</summary>
    private const int MaxPixels = 2400;

    private const double PixelsPerInch = 150; // A picture with no better idea of its size.
    private const double MaxPageShare = 0.6;

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    public static bool IsPicture(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <exception cref="NotSupportedException">The file is not a picture Windows can read.</exception>
    public static BitmapSource Load(string path) => Prepare(ImageInputLoader.LoadUpright(path));

    /// <summary>The picture on the clipboard, or null. PNG comes first: it keeps transparency, which the plain bitmap format loses.</summary>
    public static BitmapSource? FromClipboard()
    {
        if (Clipboard.GetData("PNG") is MemoryStream png)
        {
            return Prepare(BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
        }

        if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsPicture) is { } file)
        {
            return Load(file);
        }

        // The alpha channel of a clipboard bitmap is often all zero: without it the picture is not see-through.
        return Clipboard.ContainsImage() && Clipboard.GetImage() is { } image
            ? Prepare(new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0))
            : null;
    }

    /// <summary>Scaled down to at most <see cref="MaxPixels"/> on the longer side, as frozen BGRA32.</summary>
    public static BitmapSource Prepare(BitmapSource source)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest > MaxPixels)
        {
            var scale = (double)MaxPixels / longest;
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }

        return ImageConversion.ToStraightBgra(source);
    }

    /// <summary>The size on the page in points: its size at 150 dpi, at most 60% of the page either way.</summary>
    public static (double Width, double Height) FitToPage(BitmapSource image, double pageWidth, double pageHeight)
    {
        var width = image.PixelWidth * 72 / PixelsPerInch;
        var height = image.PixelHeight * 72 / PixelsPerInch;
        var scale = Math.Min(1, Math.Min(pageWidth * MaxPageShare / width, pageHeight * MaxPageShare / height));
        return (width * scale, height * scale);
    }
}
