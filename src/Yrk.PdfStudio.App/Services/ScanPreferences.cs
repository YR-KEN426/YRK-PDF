using System.IO;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.App.Services;

public sealed record ScanPaperPreference(string Id = "Original", double WidthMm = 0, double HeightMm = 0)
{
    public ScanPaperSize? Size => Id == "Custom" ? ScanPaperSizes.Custom(WidthMm, HeightMm, false)
        : ScanPaperSizes.Standard.FirstOrDefault(p => p.Id == Id);
}

public static class ScanPreferences
{
    public static string SaveFolder(string? remembered, string? documentPath, string documents, Func<string, bool>? exists = null)
    {
        exists ??= Directory.Exists;
        if (!string.IsNullOrWhiteSpace(remembered) && exists(remembered)) return remembered;
        var folder = string.IsNullOrWhiteSpace(documentPath) ? null : Path.GetDirectoryName(documentPath);
        return folder is not null && exists(folder) ? folder : documents;
    }
}
