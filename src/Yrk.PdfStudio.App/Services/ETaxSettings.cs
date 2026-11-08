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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastNumber { get; set; }

    public Dictionary<TaxDocumentType, string> LastNumbers { get; set; } = [];

    public TaxDocumentType LastInvoiceType { get; set; } = TaxDocumentType.TaxInvoice;

    public string? LastNumberFor(TaxDocumentType type) => LastNumbers.GetValueOrDefault(type);

    public void SetLastNumber(TaxDocumentType type, string number) => LastNumbers[type] = number;

    public static bool IsInvoiceType(TaxDocumentType type) => type is TaxDocumentType.TaxInvoice
        or TaxDocumentType.InvoiceTaxInvoice or TaxDocumentType.ReceiptTaxInvoice or TaxDocumentType.DeliveryTaxInvoice;

    public TaxDocumentType Type { get; set; } = TaxDocumentType.TaxInvoice;

    public decimal VatRate { get; set; } = 7;

    public bool PricesIncludeVat { get; set; }

    public static ETaxSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new();
            var text = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<ETaxSettings>(text, Json) ?? new();
            settings.LastNumbers ??= [];
            if (settings.LastNumbers.Count == 0 && !string.IsNullOrWhiteSpace(settings.LastNumber))
                settings.SetLastNumber(settings.Type, settings.LastNumber);
            settings.LastNumber = null;
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && !document.RootElement.TryGetProperty(nameof(LastInvoiceType), out _) && IsInvoiceType(settings.Type))
                settings.LastInvoiceType = settings.Type;
            if (!IsInvoiceType(settings.LastInvoiceType)) settings.LastInvoiceType = TaxDocumentType.TaxInvoice;
            return settings;
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
    public static string NextNumber(string? last, TaxDocumentType type = TaxDocumentType.TaxInvoice)
    {
        if (string.IsNullOrWhiteSpace(last))
        {
            string prefix;
            switch (type)
            {
                case TaxDocumentType.CreditNote: prefix = "CN"; break;
                case TaxDocumentType.DebitNote: prefix = "DN"; break;
                default: prefix = "INV"; break;
            }
            return prefix + DateTime.Today.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture) + "-0001";
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
