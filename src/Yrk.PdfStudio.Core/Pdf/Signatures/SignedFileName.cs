namespace Yrk.PdfStudio.Core.Pdf.Signatures;

public static class SignedFileName
{
    public static string Suggest(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.EndsWith(" (signed)", StringComparison.OrdinalIgnoreCase))
        {
            name += " (signed)";
        }

        return name + ".pdf";
    }

    public static string SuggestBeforeSigning(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        const string signedSuffix = " (signed)";
        if (name.EndsWith(signedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^signedSuffix.Length];
        }

        return name + " (before signing).pdf";
    }
}
