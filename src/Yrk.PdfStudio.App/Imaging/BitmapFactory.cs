using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Imaging;

internal static class BitmapFactory
{
    public static BitmapSource Create(PdfBitmap bitmap)
    {
        // PDFium renders opaque BGRx, which maps directly onto WPF's Bgr32.
        var source = BitmapSource.Create(
            bitmap.Width,
            bitmap.Height,
            96,
            96,
            PixelFormats.Bgr32,
            null,
            bitmap.Pixels,
            bitmap.Stride);
        source.Freeze();
        return source;
    }
}
