using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>Handwriting fonts for typed signatures, and imported photos or scans as transparent bitmaps.</summary>
internal static class SignatureImaging
{
    private const int MaxImportWidth = 1600;

    /// <summary>Handwriting-style fonts offered for typed signatures, if installed.</summary>
    private static readonly string[] TypedFontCandidates =
    [
        "Charm", "Mali", "Sriracha", "Itim",                  // Thai (Google Fonts, OFL)
        "Dancing Script", "Great Vibes",                      // Latin (Google Fonts, OFL)
        "Ink Free", "Segoe Script", "Segoe Print",            // Shipped with Windows
        "Lucida Handwriting", "Freestyle Script", "Brush Script MT",
    ];

    public static IReadOnlyList<FontFamily> AvailableTypedFonts()
    {
        var installed = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fonts = TypedFontCandidates.Where(installed.Contains).Select(name => new FontFamily(name)).ToList();
        fonts.Add(new FontFamily("Leelawadee UI")); // Always present on Windows 10/11; covers Thai.
        return fonts;
    }

    /// <summary>
    /// Loads a photo or scan of a signature and makes the paper transparent: near-white pixels become
    /// fully transparent and light grey ones partly transparent, which keeps the ink edges smooth.
    /// </summary>
    public static BitmapSource FromImageFile(string path)
    {
        BitmapSource image;
        using (var stream = File.OpenRead(path))
        {
            image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }

        if (image.PixelWidth > MaxImportWidth)
        {
            var scale = (double)MaxImportWidth / image.PixelWidth;
            image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        }

        return RemovePaper(image);
    }

    /// <summary>Makes white paper transparent and trims it (photos, scans, rendered PDF signatures).</summary>
    public static BitmapSource RemovePaper(BitmapSource image)
    {
        var bgra = ImageConversion.ToStraightBgra(image);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);

        const double paper = 225, inkLimit = 170;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var luminance = (0.114 * pixels[i]) + (0.587 * pixels[i + 1]) + (0.299 * pixels[i + 2]);
            if (luminance >= paper)
            {
                pixels[i + 3] = 0;
            }
            else if (luminance > inkLimit)
            {
                pixels[i + 3] = (byte)(pixels[i + 3] * (paper - luminance) / (paper - inkLimit));
            }
        }

        var cleaned = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        cleaned.Freeze();
        return ImageConversion.Trim(cleaned);
    }
}
