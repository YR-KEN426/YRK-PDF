using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>JPEG decoding and encoding for "Compress PDF", using WIC through WPF. Thread-safe.</summary>
public sealed class WpfImageCodec : IPdfImageCodec
{
    public PdfRawPixels? DecodeJpeg(byte[] data)
    {
        BitmapSource frame;
        try
        {
            using var stream = new MemoryStream(data);
            frame = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or OverflowException)
        {
            return null;
        }

        // CMYK JPEGs in PDFs are often stored inverted (Adobe); leave them untouched.
        PdfPixelFormat format;
        if (frame.Format == PixelFormats.Gray8)
        {
            format = PdfPixelFormat.Gray8;
        }
        else if (frame.Format == PixelFormats.Bgr24 || frame.Format == PixelFormats.Bgr32 || frame.Format == PixelFormats.Rgb24)
        {
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
            format = PdfPixelFormat.Bgr24;
        }
        else
        {
            return null;
        }

        var bytesPerPixel = format == PdfPixelFormat.Gray8 ? 1 : 3;
        var stride = frame.PixelWidth * bytesPerPixel;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        return new PdfRawPixels(frame.PixelWidth, frame.PixelHeight, stride, format, pixels);
    }

    public byte[] EncodeJpeg(PdfRawPixels pixels, int targetWidth, int targetHeight, int quality)
    {
        var format = pixels.Format switch
        {
            PdfPixelFormat.Gray8 => PixelFormats.Gray8,
            PdfPixelFormat.Bgr24 => PixelFormats.Bgr24,
            PdfPixelFormat.Bgrx32 => PixelFormats.Bgr32,
            _ => PixelFormats.Bgra32,
        };

        BitmapSource source = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, format, null, pixels.Pixels, pixels.Stride);
        if (format == PixelFormats.Bgra32)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0); // JPEG has no alpha.
        }

        if (targetWidth != pixels.Width || targetHeight != pixels.Height)
        {
            source = new TransformedBitmap(source, new ScaleTransform((double)targetWidth / pixels.Width, (double)targetHeight / pixels.Height));
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
