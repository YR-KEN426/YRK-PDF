using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yrk.PdfStudio.Core.Pdf.ETax;

namespace Yrk.PdfStudio.App.Services;

/// <summary>The seller's details and the last document number, so the next e-Tax Invoice starts filled in.</summary>
public sealed class ETaxSettings
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "etax.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All), // Thai readable in the file.
        WriteIndented = true,
    };

    public TaxParty? Seller { get; set; }

    public string? LastNumber { get; set; }

    public TaxDocumentType Type { get; set; } = TaxDocumentType.TaxInvoice;

    public decimal VatRate { get; set; } = 7;

    public bool PricesIncludeVat { get; set; }

    public static ETaxSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ETaxSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a convenience.
        }
    }

    /// <summary>The number after <paramref name="last"/>: its last run of digits counted up, keeping the zeros ("INV-0009" → "INV-0010").</summary>
    public static string NextNumber(string? last)
    {
        if (string.IsNullOrWhiteSpace(last))
        {
            return "INV" + DateTime.Today.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture) + "-0001";
        }

        var end = last.Length;
        while (end > 0 && !char.IsAsciiDigit(last[end - 1]))
        {
            end--;
        }

        var start = end;
        while (start > 0 && char.IsAsciiDigit(last[start - 1]))
        {
            start--;
        }

        if (start == end)
        {
            return last + "-1";
        }

        var digits = last[start..end];
        var next = (decimal.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits.Length, '0');
        return last[..start] + next + last[end..];
    }
}
