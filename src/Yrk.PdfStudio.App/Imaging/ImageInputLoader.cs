using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>
/// Prepares image files for "Images to PDF". JPEG photos are embedded as they are (no quality loss,
/// small files) unless their EXIF orientation says they must be turned, as phone photos often do;
/// other formats are decoded and stored losslessly.
/// </summary>
internal static class ImageInputLoader
{
    private const string OrientationQuery = "/app1/ifd/{ushort=274}";

    public static PdfImageInput Load(string path)
    {
        var data = File.ReadAllBytes(path);
        BitmapDecoder decoder;
        try
        {
            decoder = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException)
        {
            throw new NotSupportedException(Loc.F("\"{0}\" is not an image format Windows can read.", Path.GetFileName(path)), ex);
        }

        var frame = decoder.Frames[0];
        var orientation = ReadOrientation(frame);
        var isJpeg = decoder is JpegBitmapDecoder;
        var isCmyk = frame.Format == PixelFormats.Cmyk32;

        if (isJpeg && orientation <= 1 && !isCmyk)
        {
            return new PdfJpegInput(data, frame.PixelWidth, frame.PixelHeight);
        }

        var upright = ApplyOrientation(frame, orientation);
        if (isJpeg)
        {
            // Re-encode at high quality so the page stays small.
            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(upright, PixelFormats.Bgr24, null, 0)));
            using var output = new MemoryStream();
            encoder.Save(output);
            return new PdfJpegInput(output.ToArray(), upright.PixelWidth, upright.PixelHeight);
        }

        return new PdfBitmapInput(ImageConversion.ToPdfImage(upright));
    }

    /// <summary>The picture in a file, turned upright as its EXIF orientation says.</summary>
    /// <exception cref="NotSupportedException">The file is not an image Windows can read.</exception>
    public static BitmapSource LoadUpright(string path)
    {
        try
        {
            var frame = BitmapDecoder.Create(new MemoryStream(File.ReadAllBytes(path)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            return ApplyOrientation(frame, ReadOrientation(frame));
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException)
        {
            throw new NotSupportedException(Loc.F("\"{0}\" is not an image format Windows can read.", Path.GetFileName(path)), ex);
        }
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            return frame.Metadata is BitmapMetadata metadata && metadata.GetQuery(OrientationQuery) is ushort value ? value : 1;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return 1;
        }
    }

    /// <summary>EXIF orientations 2–8: mirror and/or rotate so the picture is upright.</summary>
    private static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        var (angle, mirror) = orientation switch
        {
            2 => (0, true),
            3 => (180, false),
            4 => (180, true),
            5 => (90, true),
            6 => (90, false),
            7 => (270, true),
            8 => (270, false),
            _ => (0, false),
        };
        if (angle == 0 && !mirror)
        {
            return source;
        }

        var transform = new TransformGroup();
        if (mirror)
        {
            transform.Children.Add(new ScaleTransform(-1, 1));
        }

        if (angle != 0)
        {
            transform.Children.Add(new RotateTransform(angle));
        }

        var result = new TransformedBitmap(source, transform);
        result.Freeze();
        return result;
    }
}
