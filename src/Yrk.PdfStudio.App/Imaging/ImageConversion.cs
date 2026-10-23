using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Imaging;

internal static class ImageConversion
{
    /// <summary>Converts to tightly packed BGRA32 with straight alpha, the layout PDFium expects.</summary>
    public static PdfImage ToPdfImage(BitmapSource source)
    {
        var bgra = ToStraightBgra(source);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return new PdfImage(bgra.PixelWidth, bgra.PixelHeight, pixels);
    }

    /// <summary>Returns a frozen BGRA32 (straight alpha) copy; WPF un-premultiplies Pbgra32 on conversion.</summary>
    public static BitmapSource ToStraightBgra(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32 && source.IsFrozen)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>Renders a visual onto a transparent bitmap of the given pixel size.</summary>
    public static BitmapSource Render(Visual visual, int pixelWidth, int pixelHeight)
    {
        var target = new RenderTargetBitmap(Math.Max(1, pixelWidth), Math.Max(1, pixelHeight), 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return ToStraightBgra(target);
    }

    /// <summary>Crops fully transparent margins, keeping <paramref name="padding"/> pixels around the content.</summary>
    public static BitmapSource Trim(BitmapSource source, int padding = 6)
    {
        var bgra = ToStraightBgra(source);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);

        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (pixels[(((y * width) + x) * 4) + 3] > 10)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        if (right < 0)
        {
            return bgra;
        }

        left = Math.Max(0, left - padding);
        top = Math.Max(0, top - padding);
        right = Math.Min(width - 1, right + padding);
        bottom = Math.Min(height - 1, bottom + padding);
        var cropped = new CroppedBitmap(bgra, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
        cropped.Freeze();
        return ToStraightBgra(cropped);
    }
}
