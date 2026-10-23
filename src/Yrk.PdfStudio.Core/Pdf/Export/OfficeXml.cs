using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Yrk.PdfStudio.Core.Pdf.Export;

/// <summary>Shared helpers for writing the Office Open XML (ISO/IEC 29500) packages .docx and .xlsx.</summary>
internal static partial class OfficeXml
{
    public const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    public static void AddPart(ZipArchive zip, string path, string xml)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(xml);
        stream.Write(bytes);
    }

    public static void AddPart(ZipArchive zip, string path, byte[] data)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.NoCompression).Open();
        stream.Write(data);
    }

    /// <summary>Escapes text for XML and drops characters XML 1.0 cannot hold (stray control codes in PDFs).</summary>
    public static string Escape(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': result.Append("&amp;"); break;
                case '<': result.Append("&lt;"); break;
                case '>': result.Append("&gt;"); break;
                case '"': result.Append("&quot;"); break;
                case '\t' or '\n' or '\r': result.Append(c); break;
                case < ' ' or '￾' or '￿': break;
                default: result.Append(c); break;
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// A font family name Word or Excel will recognise from a PDF font name:
    /// "ABCDEF+THSarabunNew-Bold" → "TH Sarabun New", "TimesNewRomanPSMT" → "Times New Roman", "Arial,Bold" → "Arial".
    /// </summary>
    public static string? FontFamily(string pdfFontName)
    {
        var name = pdfFontName;
        var plus = name.IndexOf('+');
        if (plus >= 0)
        {
            name = name[(plus + 1)..];
        }

        name = name.Split(['-', ','], 2)[0];
        if (KnownNames.TryGetValue(new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant(), out var known))
        {
            return known;
        }

        foreach (var suffix in new[] { "PSMT", "MT", "PS" })
        {
            if (name.Length > suffix.Length + 2 && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        name = name.Contains(' ') ? name : CamelBreaks().Replace(name, " ");
        return name.Trim().Length == 0 ? null : name.Trim();
    }

    /// <summary>Fonts whose names cannot be told from the PDF name by word breaks (the Thai government fonts above all).</summary>
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.Ordinal)
    {
        ["thsarabunpsk"] = "TH SarabunPSK",
        ["thsarabunnew"] = "TH Sarabun New",
        ["thsarabunit๙"] = "TH SarabunIT๙",
        ["thsarabunit9"] = "TH SarabunIT๙",
        ["thniramitas"] = "TH Niramit AS",
        ["thchakrapetch"] = "TH Chakra Petch",
        ["thcharmonman"] = "TH Charmonman",
        ["thbaijam"] = "TH Baijam",
        ["thkodchasal"] = "TH Kodchasal",
        ["thkoho"] = "TH KoHo",
        ["thkrub"] = "TH Krub",
        ["thsrisakdi"] = "TH Srisakdi",
        ["thfahkwang"] = "TH Fahkwang",
        ["thmaligrade6"] = "TH Mali Grade6",
        ["angsanaupc"] = "AngsanaUPC",
        ["cordiaupc"] = "CordiaUPC",
        ["browalliaupc"] = "BrowalliaUPC",
        ["dilleniaupc"] = "DilleniaUPC",
        ["eucrosiaupc"] = "EucrosiaUPC",
        ["freesiaupc"] = "FreesiaUPC",
        ["irisupc"] = "IrisUPC",
        ["jasmineupc"] = "JasmineUPC",
        ["kodchiangupc"] = "KodchiangUPC",
        ["lilyupc"] = "LilyUPC",
        ["leelawadeeui"] = "Leelawadee UI",
    };

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelBreaks();
}
