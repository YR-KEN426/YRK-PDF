namespace Yrk.PdfStudio.Core.Pdf.Scan;

public sealed record ScanExtent(double WidthMm, double HeightMm);
public sealed record ScanPaperSize(string Id, string Name, double WidthMm, double HeightMm, string Group)
{
    public bool Fits(ScanExtent? extent) => extent is null || WidthMm <= extent.WidthMm + .1 && HeightMm <= extent.HeightMm + .1;
    public NormalizedRect Crop(int width, int height, double dpiX, double dpiY) =>
        new(0, 0, Math.Min(1, WidthMm / 25.4 * dpiX / width), Math.Min(1, HeightMm / 25.4 * dpiY / height));
}

public static class ScanPaperSizes
{
    public static IEnumerable<ScanPaperSize> Visible(ScanExtent? extent, string rememberedId) =>
        Standard.Where(p => p.Fits(extent) || p.Id == rememberedId);

    public static IReadOnlyList<ScanPaperSize> Standard { get; } = Array.AsReadOnly<ScanPaperSize>([
        new("A3", "ISO A3", 297, 420, "ISO A"),
        new("A4", "ISO A4", 210, 297, "ISO A"),
        new("A5", "ISO A5", 148, 210, "ISO A"),
        new("A6", "ISO A6", 105, 148, "ISO A"),
        new("JisB4", "JIS B4", 257, 364, "B"),
        new("JisB5", "JIS B5", 182, 257, "B"),
        new("IsoB5", "ISO B5", 176, 250, "B"),
        new("Letter", "US Letter", 215.9, 279.4, "US"),
        new("Legal", "US Legal", 215.9, 355.6, "US"),
        new("Executive", "US Executive", 184.2, 266.7, "US"),
        new("Tabloid", "Tabloid / Ledger", 279.4, 431.8, "US"),
        new("Statement", "Statement / Half Letter", 139.7, 215.9, "US"),
        new("F4", "F4 / Folio", 210, 330, "Other"),
        new("Oficio", "Oficio", 215.9, 340, "Other"),
        new("Photo4x6", "Photo 4 × 6 in", 101.6, 152.4, "Photo"),
        new("Photo5x7", "Photo 5 × 7 in", 127, 177.2, "Photo"),
        new("BusinessCard", "Business card", 88.9, 50.8, "Card"),
        new("IdCard", "ID card CR80", 85.6, 54, "Card")
    ]);

    public static ScanPaperSize Custom(double width, double height, bool inches, ScanExtent? extent = null)
    {
        var size = new ScanPaperSize("Custom", "Custom…", width * (inches ? 25.4 : 1), height * (inches ? 25.4 : 1), "Custom");
        if (!double.IsFinite(size.WidthMm + size.HeightMm) || size.WidthMm <= 0 || size.HeightMm <= 0 || !size.Fits(extent))
            throw new ArgumentOutOfRangeException(nameof(width));
        return size;
    }
}
