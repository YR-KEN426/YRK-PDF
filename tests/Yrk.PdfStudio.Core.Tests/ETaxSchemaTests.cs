using System.Xml;
using System.Xml.Schema;
using Yrk.PdfStudio.Core.Pdf.ETax;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ETaxSchemaTests
{
    [Fact]
    public void Discounted_tax_invoice_matches_the_official_schema_when_available()
    {
        var directory = Environment.GetEnvironmentVariable("YRK_ETAX_XSD_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            // Optional local fixture: the official schemas must never be copied into this repository.
            return;
        }

        var invoice = new TaxInvoice(
            TaxDocumentType.TaxInvoice, "INV-001", new DateTime(2026, 11, 7),
            new TaxParty("Seller", TaxIdKind.TaxId, "0105536000313", "00000", "Street", "10310",
                BuildingNumber: "99/9", Province: "10", District: "1017", Subdistrict: "101701"),
            new TaxParty("Buyer", TaxIdKind.NationalId, "1104599001276", string.Empty, "Street", "10330"),
            [new("A", 2, "EA", 100, new(10, true)), new("B", 1, "EA", 100, new(5))],
            Discount: new(20));

        Validate(invoice.ToXml(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.FromHours(7))), directory, "TaxInvoice");
    }

    [Theory]
    [InlineData(TaxDocumentType.DebitNote, "DBNG02", null, false)]
    [InlineData(TaxDocumentType.DebitNote, "DBNS99", "Service adjustment", true)]
    [InlineData(TaxDocumentType.CreditNote, "CDNG04", null, false)]
    [InlineData(TaxDocumentType.CreditNote, "CDNS99", "Service adjustment", true)]
    public void Debit_and_credit_notes_match_the_official_schema_when_available(TaxDocumentType type, string reasonCode, string? reasonText, bool discounted)
    {
        var directory = Environment.GetEnvironmentVariable("YRK_ETAX_XSD_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            // Same optional fixture as the invoice test; schemas stay outside the repository.
            return;
        }

        var invoice = new TaxInvoice(
            type, "ADJ-001", new DateTime(2026, 11, 7),
            new TaxParty("Seller", TaxIdKind.TaxId, "0105536000313", "00000", "Street", "10310",
                BuildingNumber: "99/9", Province: "10", District: "1017", Subdistrict: "101701"),
            new TaxParty("Buyer", TaxIdKind.NationalId, "1104599001276", string.Empty, "Street", "10330"),
            [new("A", 2, "EA", 100, discounted ? new(10, true) : null), new("B", 1, "EA", 100)],
            Discount: discounted ? new(20) : null,
            Reference: new("INV-001", new DateTime(2026, 11, 1), TaxDocumentType.TaxInvoice, 1000),
            Reason: new(reasonCode, reasonText));

        Assert.Empty(invoice.Problems());
        Validate(invoice.ToXml(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.FromHours(7))), directory, "DebitCreditNote");
    }

    /// <summary>Validates either TaxInvoice or DebitCreditNote XML using an externally supplied official schema bundle.</summary>
    internal static void Validate(byte[] xml, string directory, string documentKind)
    {
        var schemaName = $"{documentKind}_CrossIndustryInvoice_2p0.xsd";
        var paths = Directory.GetFiles(directory, schemaName, SearchOption.AllDirectories);
        var path = Assert.Single(paths);
        var errors = new List<string>();
        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        schemas.ValidationEventHandler += (_, e) => errors.Add(e.Message);
        schemas.Add(null, path);
        schemas.Compile();

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            ValidationType = ValidationType.Schema,
            Schemas = schemas,
        };
        settings.ValidationFlags |= XmlSchemaValidationFlags.ReportValidationWarnings;
        settings.ValidationEventHandler += (_, e) => errors.Add(e.Message);
        using var input = new MemoryStream(xml);
        using var reader = XmlReader.Create(input, settings);
        while (reader.Read())
        {
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}
