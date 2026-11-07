using System.Text;
using System.Xml.Linq;
using System.Security.Cryptography;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Archive;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.ETax;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")] // Only one System.Windows.Application can exist at a time; shared with MainWindowLayoutTests.
public sealed class ETaxTests : IDisposable
{
    private static readonly XNamespace Ram = "urn:etda:uncefact:data:standard:TaxInvoice_ReusableAggregateBusinessInformationEntity:2";
    private static readonly XNamespace DcnRam = "urn:etda:uncefact:data:standard:DebitCreditNote_ReusableAggregateBusinessInformationEntity:2";
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-etax-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Theory]
    [InlineData("0105536000313", true)]
    [InlineData("0-1055-36000-31-3", true)]
    [InlineData("0105536000315", false)]
    [InlineData("010553600031", false)]
    public void Checks_the_digit_of_a_thai_tax_id(string id, bool valid) => Assert.Equal(valid, TaxInvoice.IsValidThaiId(id));

    [Theory]
    [InlineData(TaxDocumentType.DebitNote, "80", "ใบเพิ่มหนี้", 1243)]
    [InlineData(TaxDocumentType.CreditNote, "81", "ใบลดหนี้", 757)]
    public void Writes_debit_and_credit_notes_in_schema_order(TaxDocumentType type, string code, string name, decimal correct)
    {
        var invoice = Adjustment(type) with { Note = " Note " };
        var created = new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.FromHours(7));
        var xml = XDocument.Parse(Encoding.UTF8.GetString(invoice.ToXml(created)));
        XNamespace rsm = "urn:etda:uncefact:data:standard:DebitCreditNote_CrossIndustryInvoice:2";
        Assert.Equal(rsm + "DebitCreditNote_CrossIndustryInvoice", xml.Root!.Name);
        Assert.Equal("rsm", xml.Root.GetPrefixOfNamespace(rsm));
        Assert.Equal("ram", xml.Root.GetPrefixOfNamespace(DcnRam));
        Assert.Equal(["ExchangedDocumentContext", "ExchangedDocument", "SupplyChainTradeTransaction"], xml.Root.Elements().Select(e => e.Name.LocalName));
        Assert.All(xml.Root.Elements(), e => Assert.Equal(rsm, e.Name.Namespace));
        Assert.All(xml.Root.Elements().SelectMany(e => e.Descendants()), e => Assert.Equal(DcnRam, e.Name.Namespace));
        var document = xml.Root.Element(rsm + "ExchangedDocument")!;
        Assert.Equal(["ID", "Name", "TypeCode", "IssueDateTime", "Purpose", "PurposeCode", "CreationDateTime", "IncludedNote"],
            document.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(code, invoice.TypeCode);
        Assert.Equal(name, invoice.ThaiName);
        Assert.Equal(name, document.Element(DcnRam + "Name")!.Value);
        Assert.Equal(code, document.Element(DcnRam + "TypeCode")!.Value);
        Assert.Equal("Price adjustment", document.Element(DcnRam + "Purpose")!.Value);
        Assert.Equal(invoice.Reason!.Code, document.Element(DcnRam + "PurposeCode")!.Value);
        Assert.Equal("2026-11-07T00:00:00", document.Element(DcnRam + "IssueDateTime")!.Value);
        Assert.Equal("2026-11-07T09:00:00.000+07:00", document.Element(DcnRam + "CreationDateTime")!.Value);
        var agreement = xml.Descendants(DcnRam + "ApplicableHeaderTradeAgreement").Single();
        Assert.Equal(["SellerTradeParty", "BuyerTradeParty", "AdditionalReferencedDocument"], agreement.Elements().Select(e => e.Name.LocalName));
        var reference = agreement.Element(DcnRam + "AdditionalReferencedDocument")!;
        Assert.Equal(["IssuerAssignedID", "IssueDateTime", "ReferenceTypeCode"], reference.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(["INV-001", "2026-11-01T00:00:00", "388"], reference.Elements().Select(e => e.Value));
        var header = xml.Descendants(DcnRam + "ApplicableHeaderTradeSettlement").Single();
        Assert.Equal(["InvoiceCurrencyCode", "ApplicableTradeTax", "SpecifiedTradeAllowanceCharge", "SpecifiedTradeSettlementHeaderMonetarySummation"],
            header.Elements().Select(e => e.Name.LocalName));
        var sums = header.Element(DcnRam + "SpecifiedTradeSettlementHeaderMonetarySummation")!;
        Assert.Equal(["OriginalInformationAmount", "LineTotalAmount", "DifferenceInformationAmount", "AllowanceTotalAmount", "TaxBasisTotalAmount", "TaxTotalAmount", "GrandTotalAmount"],
            sums.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(new[] { 1000m, correct, 243m, 27m, 243m, 17.01m, 260.01m }, sums.Elements().Select(XmlAmount));
        Assert.Equal(243m, XmlAmount(header.Element(DcnRam + "ApplicableTradeTax")!.Element(DcnRam + "BasisAmount")!));
        Assert.Equal(new[] { 180m, 90m }, xml.Descendants(DcnRam + "NetLineTotalAmount").Select(XmlAmount));
        Assert.Equal(1000m, invoice.OriginalValue);
        Assert.Equal(correct, invoice.CorrectValue);
        Assert.Equal(243m, invoice.Difference);
        Assert.Equal(260.01m, invoice.GrandTotal);
        Assert.Equal(invoice.Vat, invoice.LineVats().Sum());
        Assert.Empty(invoice.Problems());
    }

    [Theory]
    [InlineData(TaxDocumentType.DebitNote, false, 1300, 300, 21, 321)]
    [InlineData(TaxDocumentType.CreditNote, false, 700, 300, 21, 321)]
    [InlineData(TaxDocumentType.DebitNote, true, 1810, 810, 56.70, 866.70)]
    [InlineData(TaxDocumentType.CreditNote, true, 190, 810, 56.70, 866.70)]
    public void Calculates_adjustments_with_and_without_vat_inclusive_discounts(TaxDocumentType type, bool includesVat, decimal correct, decimal difference, decimal vat, decimal grand)
    {
        var invoice = Adjustment(type) with
        {
            Lines = includesVat ? [new("A", 1, "EA", 1070, new(10, true))] : [new("A", 1, "EA", 300)],
            Discount = includesVat ? new(96.30m) : null,
            PricesIncludeVat = includesVat,
        };
        Assert.Equal(correct, invoice.CorrectValue);
        Assert.Equal(difference, invoice.Difference);
        Assert.Equal(difference, invoice.TaxBasis);
        Assert.Equal(vat, invoice.Vat);
        Assert.Equal(grand, invoice.GrandTotal);
        Assert.Empty(invoice.Problems());
        var xml = XDocument.Parse(Encoding.UTF8.GetString(invoice.ToXml(DateTimeOffset.Now)));
        var sums = xml.Descendants(DcnRam + "SpecifiedTradeSettlementHeaderMonetarySummation").Single();
        Assert.Equal(correct, XmlAmount(sums.Element(DcnRam + "LineTotalAmount")!));
        Assert.Equal(difference, XmlAmount(sums.Element(DcnRam + "DifferenceInformationAmount")!));
        Assert.Equal(grand, XmlAmount(sums.Element(DcnRam + "GrandTotalAmount")!));
        if (!includesVat)
        {
            Assert.Equal(["OriginalInformationAmount", "LineTotalAmount", "DifferenceInformationAmount", "TaxBasisTotalAmount", "TaxTotalAmount", "GrandTotalAmount"],
                sums.Elements().Select(e => e.Name.LocalName));
            Assert.Empty(xml.Descendants(DcnRam + "SpecifiedTradeAllowanceCharge"));
        }
    }

    [Theory]
    [InlineData(TaxDocumentType.TaxInvoice, "388")]
    [InlineData(TaxDocumentType.InvoiceTaxInvoice, "T02")]
    [InlineData(TaxDocumentType.ReceiptTaxInvoice, "T03")]
    [InlineData(TaxDocumentType.DeliveryTaxInvoice, "T04")]
    public void Both_adjustment_types_can_reference_each_invoice_type(TaxDocumentType originalType, string code)
    {
        foreach (var type in new[] { TaxDocumentType.DebitNote, TaxDocumentType.CreditNote })
        {
            var invoice = Adjustment(type);
            invoice = invoice with { Reference = invoice.Reference! with { Type = originalType } };
            Assert.Empty(invoice.Problems());
            var xml = XDocument.Parse(Encoding.UTF8.GetString(invoice.ToXml(DateTimeOffset.Now)));
            Assert.Equal(code, xml.Descendants(DcnRam + "ReferenceTypeCode").Single().Value);
        }
    }

    [Theory]
    [InlineData(TaxDocumentType.DebitNote, "DBNG01,DBNG02,DBNG99,DBNS01,DBNS02,DBNS99")]
    [InlineData(TaxDocumentType.CreditNote, "CDNG01,CDNG02,CDNG03,CDNG04,CDNG05,CDNG99,CDNS01,CDNS02,CDNS03,CDNS04,CDNS99")]
    public void Accepts_each_reason_only_for_its_own_document_type(TaxDocumentType type, string codes)
    {
        foreach (var code in codes.Split(','))
        {
            var invoice = Adjustment(type) with { Reason = new(code, " Explanation ") };
            Assert.Empty(invoice.Problems());
            var xml = XDocument.Parse(Encoding.UTF8.GetString(invoice.ToXml(DateTimeOffset.Now)));
            Assert.Equal("Explanation", xml.Descendants(DcnRam + "Purpose").Single().Value);
            Assert.Equal(code, xml.Descendants(DcnRam + "PurposeCode").Single().Value);
            var other = invoice with { Type = type == TaxDocumentType.DebitNote ? TaxDocumentType.CreditNote : TaxDocumentType.DebitNote };
            Assert.Equal(["The reason code is not valid for this document type."], other.Problems());
            foreach (var text in new string?[] { null, "", " " })
            {
                var withoutText = invoice with { Reason = new(code, text) };
                if (code.EndsWith("99", StringComparison.Ordinal))
                    Assert.Equal(["Enter the reason text for a code ending in 99."], withoutText.Problems());
                else
                {
                    Assert.Empty(withoutText.Problems());
                    var noPurpose = XDocument.Parse(Encoding.UTF8.GetString(withoutText.ToXml(DateTimeOffset.Now)));
                    Assert.Empty(noPurpose.Descendants(DcnRam + "Purpose"));
                    var document = noPurpose.Root!.Elements().Single(e => e.Name.LocalName == "ExchangedDocument");
                    Assert.Equal(["ID", "Name", "TypeCode", "IssueDateTime", "PurposeCode", "CreationDateTime"], document.Elements().Select(e => e.Name.LocalName));
                }
            }
        }
    }

    [Theory]
    [InlineData(TaxDocumentType.DebitNote)]
    [InlineData(TaxDocumentType.CreditNote)]
    public void Reports_each_missing_or_invalid_adjustment_field(TaxDocumentType type)
    {
        var invoice = Adjustment(type);
        var reference = invoice.Reference!;
        Assert.Equal(["Enter the original invoice number.", "Enter the original invoice date.", "Enter the original invoice value before VAT, above zero."],
            (invoice with { Reference = null }).Problems());
        Assert.Equal(["Enter the original invoice number."], (invoice with { Reference = reference with { Number = " " } }).Problems());
        Assert.Equal(["Enter the original invoice date."], (invoice with { Reference = reference with { IssueDate = default } }).Problems());
        foreach (var value in new[] { 0m, -1m })
            Assert.Contains("Enter the original invoice value before VAT, above zero.", (invoice with { Reference = reference with { OriginalValue = value } }).Problems());
        foreach (var originalType in new[] { TaxDocumentType.DebitNote, TaxDocumentType.CreditNote, (TaxDocumentType)999 })
            Assert.Equal(["The original document must be a tax invoice (388, T02, T03 or T04)."], (invoice with { Reference = reference with { Type = originalType } }).Problems());
        Assert.Equal(["Enter the debit or credit note reason code."], (invoice with { Reason = null }).Problems());
        foreach (var code in new[] { "", " " })
            Assert.Equal(["Enter the debit or credit note reason code."], (invoice with { Reason = new(code) }).Problems());
        foreach (var code in new[] { "UNKNOWN", "DBNG03", "DBNS03", "CDNG06", "CDNS05", "dbng01", " DBNG01 " })
            Assert.Equal(["The reason code is not valid for this document type."], (invoice with { Reason = new(code) }).Problems());
    }

    [Fact]
    public void Credit_notes_may_reduce_the_whole_original_value_but_no_more()
    {
        var invoice = Adjustment(TaxDocumentType.CreditNote);
        var full = invoice with { Reference = invoice.Reference! with { OriginalValue = 243 } };
        Assert.Empty(full.Problems());
        Assert.Equal(0m, full.CorrectValue);
        var excess = full with { Reference = full.Reference! with { OriginalValue = 242.99m } };
        Assert.Equal(["The credit note cannot reduce more than the original invoice value."], excess.Problems());
        Assert.Empty((excess with { Type = TaxDocumentType.DebitNote, Reason = new("DBNG01") }).Problems());
    }

    [Theory]
    [InlineData(TaxDocumentType.TaxInvoice)]
    [InlineData(TaxDocumentType.InvoiceTaxInvoice)]
    [InlineData(TaxDocumentType.ReceiptTaxInvoice)]
    [InlineData(TaxDocumentType.DeliveryTaxInvoice)]
    public void Other_document_types_reject_reference_and_reason(TaxDocumentType type)
    {
        var invoice = Adjustment(TaxDocumentType.DebitNote) with { Type = type };
        Assert.Equal(["Only debit and credit notes can reference an original invoice.", "Only debit and credit notes can have a reason."], invoice.Problems());
        Assert.Equal(["Only debit and credit notes can reference an original invoice."], (invoice with { Reason = null }).Problems());
        Assert.Equal(["Only debit and credit notes can have a reason."], (invoice with { Reference = null }).Problems());
        var xml = XDocument.Parse(Encoding.UTF8.GetString(invoice.ToXml(DateTimeOffset.Now)));
        foreach (var name in new[] { "Purpose", "PurposeCode", "AdditionalReferencedDocument", "OriginalInformationAmount", "DifferenceInformationAmount" })
            Assert.Empty(xml.Descendants(Ram + name));
        Assert.Same(Sample().XmpExtension, invoice.XmpExtension);
        Assert.Equal("Tax Invoice", invoice.XmpExtension.Properties.Single(p => p.Name == "DocumentType").Value);
    }

    [Theory]
    [InlineData(TaxDocumentType.DebitNote, "Debit Note")]
    [InlineData(TaxDocumentType.CreditNote, "Credit Note")]
    public async Task Attaches_adjustment_xml_to_pdf_a3_with_its_document_type(TaxDocumentType type, string documentType)
    {
        var invoice = Adjustment(type);
        var created = new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.FromHours(7));
        var xml = invoice.ToXml(created);
        var extension = invoice.XmpExtension;
        Assert.Equal("Tax Invoice", Sample().XmpExtension.Properties.Single(p => p.Name == "DocumentType").Value);
        var source = PdfDrawing.Create([new PdfPageDrawing(200, 200, [])]);
        var pdf = Sta.Run(() => PdfA.Convert(source, new App.Imaging.WpfPdfAFontSource(), now: created,
            attachments: [new PdfAAttachment(TaxInvoice.XmlFileName, xml, "text/xml", "Source")], extension: extension).Pdf);
        var path = Path.Combine(_folder.FullName, $"{type}.pdf");
        await File.WriteAllBytesAsync(path, pdf);
        await using (var document = await _engine.OpenAsync(path))
            Assert.Equal(1, document.PageCount);
        var cos = CosDocument.Load(pdf);
        var metadata = (CosStream)cos.Resolve(cos.Catalog["Metadata"])!;
        var xmp = XDocument.Parse(Encoding.UTF8.GetString(CosFilters.Decode(metadata, cos.Resolve)).Split("<?xpacket")[1].Split("?>", 2)[1]);
        XNamespace id = "http://www.aiim.org/pdfa/ns/id/";
        XNamespace rsm = "urn:etda:uncefact:data:standard:Invoice_CrossIndustryInvoice:2#";
        Assert.Equal("3", xmp.Descendants(id + "part").Single().Value);
        Assert.Equal("U", xmp.Descendants(id + "conformance").Single().Value);
        Assert.Equal(documentType, xmp.Descendants(rsm + "DocumentType").Single().Value);
        Assert.Equal(TaxInvoice.XmlFileName, xmp.Descendants(rsm + "DocumentFileName").Single().Value);
        Assert.Equal("2.0", xmp.Descendants(rsm + "Version").Single().Value);
        var spec = (CosDictionary)cos.Resolve(((CosArray)cos.Catalog["AF"]!)[0])!;
        Assert.Equal("Source", ((CosName)spec["AFRelationship"]!).Value);
        var file = (CosStream)cos.Resolve(((CosDictionary)spec["EF"]!)["F"])!;
        Assert.Equal(xml, CosFilters.Decode(file, cos.Resolve));
    }

    // SHA-256 captured from HEAD (Step 2), including the XML declaration and whitespace.
    [Theory]
    [InlineData(TaxDocumentType.TaxInvoice, "75333A1F18913D8B9C4425BA5C2C7959C3180AEB8352B34B8B8824E1EBE3A146", "78B3D2E40AF2A99E6BC6835EDF05F041DE76FFBF4E69DDC1B65147511C7E2F42")]
    [InlineData(TaxDocumentType.InvoiceTaxInvoice, "3730A166665A81CAC215958ABA0EA5825BB5BE48417789B78EFA4B33D0D24DDE", "90BEAAFEFC17F484E5EA15C576432A536118B4C3280DB37AA980AA7183BC24E3")]
    [InlineData(TaxDocumentType.ReceiptTaxInvoice, "676D7A5602474C04B401D3BCD00024C5C496502DB85CFC596699BBC502140635", "4D6727F4ADB8F209AC7AED7996F83E4C4F4BCAB59B17A2881CB65F78734ECFAF")]
    [InlineData(TaxDocumentType.DeliveryTaxInvoice, "D5E9626A9E57621F735DD088516A8EFC1452DB128FA6D21D4EDD837166C67A68", "8D26F4BFC1EC15BE9EAECD5EA46F5ADF49F9D2EB91EA505AAFC5B9E5BA779C1B")]
    public void Existing_invoice_types_keep_their_xml_bytes(TaxDocumentType type, string golden, string discountedGolden)
    {
        var invoice = new TaxInvoice(type, "INV-001", new DateTime(2026, 11, 7),
            new TaxParty("Seller", TaxIdKind.TaxId, "0105536000313", "00000", "Street", "10310",
                BuildingNumber: "99/9", Province: "10", District: "1017", Subdistrict: "101701"),
            new TaxParty("Buyer", TaxIdKind.NationalId, "1104599001276", "", "Street", "10330"),
            [new("A", 2, "EA", 100), new("B", 1, "", 50)], Note: "Note");
        var created = new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.FromHours(7));
        Assert.Equal(golden, Convert.ToHexString(SHA256.HashData(invoice.ToXml(created))));
        var discounted = invoice with { Lines = [new("A", 2, "EA", 100, new(10, true)), new("B", 1, "", 50, new(5))], Discount = new(20) };
        Assert.Equal(discountedGolden, Convert.ToHexString(SHA256.HashData(discounted.ToXml(created))));
    }

    private static TaxInvoice Adjustment(TaxDocumentType type) => Sample() with
    {
        Type = type, Number = "ADJ-001", IssueDate = new DateTime(2026, 11, 7),
        Lines = [new("A", 2, "EA", 100, new(20)), new("B", 1, "EA", 100, new(10, true))],
        Discount = new(27),
        Reference = new(" INV-001 ", new DateTime(2026, 11, 1, 15, 0, 0), TaxDocumentType.TaxInvoice, 1000),
        Reason = new(type == TaxDocumentType.DebitNote ? "DBNG02" : "CDNG04", " Price adjustment "),
    };

    [Fact]
    public void Writes_the_etda_xml_with_the_totals()
    {
        var invoice = Sample();

        var xml = XDocument.Parse(Encoding.UTF8.GetString(invoice.ToXml(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.FromHours(7)))));

        Assert.Equal("TaxInvoice_CrossIndustryInvoice", xml.Root!.Name.LocalName);
        Assert.Equal("ER3-2560", xml.Descendants(Ram + "GuidelineSpecifiedDocumentContextParameter").Single().Element(Ram + "ID")!.Value);
        Assert.Equal("T03", xml.Descendants(Ram + "TypeCode").First().Value);
        var seller = xml.Descendants(Ram + "SellerTradeParty").Single();
        var sellerId = seller.Descendants(Ram + "ID").Single();
        Assert.Equal("010553600031300001", sellerId.Value);
        Assert.Equal("TXID", sellerId.Attribute("schemeID")!.Value);
        Assert.Equal("NIDN", xml.Descendants(Ram + "BuyerTradeParty").Single().Descendants(Ram + "ID").Single().Attribute("schemeID")!.Value);
        Assert.Equal("ชั้น 5", seller.Descendants(Ram + "LineTwo").Single().Value);
        var sums = xml.Descendants(Ram + "SpecifiedTradeSettlementHeaderMonetarySummation").Single();
        Assert.Equal("1500.50", sums.Element(Ram + "LineTotalAmount")!.Value);
        Assert.Equal("105.04", sums.Element(Ram + "TaxTotalAmount")!.Value);
        Assert.Equal("1605.54", sums.Element(Ram + "GrandTotalAmount")!.Value);
        Assert.Equal(2, xml.Descendants(Ram + "IncludedSupplyChainTradeLineItem").Count());
    }

    [Fact]
    public void The_vat_of_the_lines_adds_up_to_the_vat_of_the_invoice()
    {
        var invoice = Sample() with { Lines = [new TaxInvoiceLine("ก", 1, "ชิ้น", 0.33m), new TaxInvoiceLine("ข", 1, "ชิ้น", 0.33m), new TaxInvoiceLine("ค", 1, "ชิ้น", 0.33m)] };

        Assert.Equal(0.07m, invoice.Vat);
        Assert.Equal(invoice.Vat, invoice.LineVats().Sum());

        var xml = XDocument.Parse(Encoding.UTF8.GetString(invoice.ToXml(DateTimeOffset.Now)));
        var lines = xml.Descendants(Ram + "SpecifiedTradeSettlementLineMonetarySummation").Select(s => decimal.Parse(s.Element(Ram + "TaxTotalAmount")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0.07m, lines.Sum());
    }

    [Fact]
    public void A_service_has_a_quantity_but_no_unit()
    {
        var invoice = Sample() with { Lines = [new TaxInvoiceLine("ค่าบริการ", 1, string.Empty, 100), new TaxInvoiceLine("กระดาษ", 2, "รีม", 100)] };

        var xml = XDocument.Parse(Encoding.UTF8.GetString(invoice.ToXml(DateTimeOffset.Now)));

        var quantities = xml.Descendants(Ram + "BilledQuantity").ToList();
        Assert.Equal(2, quantities.Count); // The ETDA schema requires it on every line.
        Assert.Null(quantities[0].Attribute("unitCode"));
        Assert.Equal("รีม", quantities[1].Attribute("unitCode")!.Value);
    }

    [Fact]
    public void Takes_the_vat_out_of_prices_that_include_it()
    {
        var invoice = Sample() with { Lines = [new TaxInvoiceLine("ค่าบริการ", 1, string.Empty, 1070)], PricesIncludeVat = true };

        Assert.Equal(1000m, invoice.TaxBasis);
        Assert.Equal(70m, invoice.Vat);
        Assert.Equal(1070m, invoice.GrandTotal);
        Assert.Empty(invoice.Problems());
    }

    [Theory]
    [InlineData(50, false, 150, 10.50, 160.50)]
    [InlineData(10, true, 180, 12.60, 192.60)]
    public void Discounts_the_whole_line_in_baht_or_percent(decimal value, bool isPercent, decimal amount, decimal vat, decimal grandTotal)
    {
        var line = new TaxInvoiceLine("Item", 2, "EA", 100, new TaxDiscount(value, isPercent));
        var invoice = Sample() with { Lines = [line] };

        Assert.Equal(amount, line.Amount);
        Assert.Equal(200 - amount, line.DiscountAmount);
        Assert.Equal(amount, invoice.LineTotal);
        Assert.Equal(amount, invoice.TaxBasis);
        Assert.Equal(vat, invoice.Vat);
        Assert.Equal(grandTotal, invoice.GrandTotal);
        Assert.Empty(invoice.Problems());

        var xml = XDocument.Parse(Encoding.UTF8.GetString(invoice.ToXml(DateTimeOffset.Now)));
        var settlement = xml.Descendants(Ram + "SpecifiedLineTradeSettlement").Single();
        Assert.Equal(["ApplicableTradeTax", "SpecifiedTradeAllowanceCharge", "SpecifiedTradeSettlementLineMonetarySummation"],
            settlement.Elements().Select(e => e.Name.LocalName));
        AssertAllowance(settlement.Element(Ram + "SpecifiedTradeAllowanceCharge")!, line.DiscountAmount);
        Assert.Equal(amount, XmlAmount(settlement.Descendants(Ram + "NetLineTotalAmount").Single()));
        Assert.Null(xml.Descendants(Ram + "ApplicableHeaderTradeSettlement").Single().Element(Ram + "SpecifiedTradeAllowanceCharge"));
        Assert.Empty(xml.Descendants(Ram + "AllowanceTotalAmount"));
    }

    [Theory]
    [InlineData(27, false)]
    [InlineData(10, true)]
    public void Applies_the_bill_discount_after_line_discounts(decimal value, bool isPercent)
    {
        var invoice = Sample() with
        {
            Lines = [new("A", 2, "EA", 100, new(20)), new("B", 1, "EA", 100, new(10, true))],
            Discount = new(value, isPercent),
        };

        Assert.Equal(270m, invoice.LineTotal);
        Assert.Equal(27m, invoice.DiscountAmount);
        Assert.Equal(243m, invoice.TaxBasis);
        Assert.Equal(17.01m, invoice.Vat);
        Assert.Equal(260.01m, invoice.GrandTotal);
        Assert.Equal(new[] { 11.34m, 5.67m }, invoice.LineVats());
        Assert.Empty(invoice.Problems());

        var xml = XDocument.Parse(Encoding.UTF8.GetString(invoice.ToXml(DateTimeOffset.Now)));
        var header = xml.Descendants(Ram + "ApplicableHeaderTradeSettlement").Single();
        Assert.Equal(["InvoiceCurrencyCode", "ApplicableTradeTax", "SpecifiedTradeAllowanceCharge", "SpecifiedTradeSettlementHeaderMonetarySummation"],
            header.Elements().Select(e => e.Name.LocalName));
        AssertAllowance(header.Element(Ram + "SpecifiedTradeAllowanceCharge")!, 27);
        var sums = header.Element(Ram + "SpecifiedTradeSettlementHeaderMonetarySummation")!;
        Assert.Equal(["LineTotalAmount", "AllowanceTotalAmount", "TaxBasisTotalAmount", "TaxTotalAmount", "GrandTotalAmount"],
            sums.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(new[] { 270m, 27m, 243m, 17.01m, 260.01m }, sums.Elements().Select(XmlAmount));
        Assert.Equal(new[] { 180m, 90m }, xml.Descendants(Ram + "NetLineTotalAmount").Select(XmlAmount));
        Assert.Equal(invoice.Vat, xml.Descendants(Ram + "SpecifiedLineTradeSettlement")
            .Select(s => XmlAmount(s.Element(Ram + "ApplicableTradeTax")!.Element(Ram + "CalculatedAmount")!)).Sum());
    }

    [Theory]
    [InlineData(107, false, 96.30, false)]
    [InlineData(10, true, 10, true)]
    public void Discounts_vat_inclusive_prices_before_extracting_vat(decimal lineValue, bool linePercent, decimal billValue, bool billPercent)
    {
        var invoice = Sample() with
        {
            Lines = [new("Item", 1, "EA", 1070, new(lineValue, linePercent))],
            Discount = new(billValue, billPercent),
            PricesIncludeVat = true,
        };

        Assert.Equal(963m, invoice.LineTotal);
        Assert.Equal(96.30m, invoice.DiscountAmount);
        Assert.Equal(810m, invoice.TaxBasis);
        Assert.Equal(56.70m, invoice.Vat);
        Assert.Equal(866.70m, invoice.GrandTotal);
        Assert.Equal(invoice.Vat, invoice.LineVats().Sum());
        Assert.Empty(invoice.Problems());

        var xml = XDocument.Parse(Encoding.UTF8.GetString(invoice.ToXml(DateTimeOffset.Now)));
        var header = xml.Descendants(Ram + "ApplicableHeaderTradeSettlement").Single();
        AssertAllowance(header.Element(Ram + "SpecifiedTradeAllowanceCharge")!, 96.30m);
        AssertAllowance(xml.Descendants(Ram + "SpecifiedLineTradeSettlement").Single().Element(Ram + "SpecifiedTradeAllowanceCharge")!, 107);
        Assert.Equal(810m, XmlAmount(header.Element(Ram + "ApplicableTradeTax")!.Element(Ram + "BasisAmount")!));
        Assert.Equal(866.70m, XmlAmount(xml.Descendants(Ram + "GrandTotalAmount").Single()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Distributed_bill_discounts_reconcile_line_vat_in_both_pricing_modes(bool includesVat)
    {
        var invoice = Sample() with
        {
            Lines = [new("A", 1, "EA", 0.33m), new("B", 1, "EA", 0.33m), new("C", 1, "EA", 0.33m)],
            Discount = new(10, true), PricesIncludeVat = includesVat,
        };

        Assert.Equal(0.10m, invoice.DiscountAmount);
        Assert.Equal(0.06m, invoice.Vat);
        Assert.Equal(invoice.Vat, invoice.LineVats().Sum());
        var xml = XDocument.Parse(Encoding.UTF8.GetString(invoice.ToXml(DateTimeOffset.Now)));
        Assert.Equal(invoice.Vat, xml.Descendants(Ram + "SpecifiedTradeSettlementLineMonetarySummation")
            .Select(s => XmlAmount(s.Element(Ram + "TaxTotalAmount")!)).Sum());
    }

    [Fact]
    public void Reconciles_rounding_without_negative_vat_on_a_free_last_line()
    {
        var invoice = Sample() with
        {
            Lines = [new("A", 1, "EA", 0.08m), new("B", 1, "EA", 0.08m), new("Free", 1, "EA", 0)],
            Discount = new(0.01m),
        };
        Assert.Equal(new[] { 0.01m, 0m, 0m }, invoice.LineVats());
        Assert.Equal(invoice.Vat, invoice.LineVats().Sum());
    }

    [Theory]
    [InlineData(1, 1.005, 0, false, 1.01, 0)]
    [InlineData(1, 1.005, 0.01, false, 1, 0.01)]
    [InlineData(3, 0.335, 10, true, 0.90, 0.10)]
    [InlineData(1, 1, 0.005, false, 1, 0.01)]
    [InlineData(1, 1.005, 100, true, 0, 1.01)]
    public void Rounds_after_the_line_discount_away_from_zero(decimal quantity, decimal price, decimal value, bool percent, decimal expected, decimal allowance)
    {
        var line = new TaxInvoiceLine("Item", quantity, "EA", price, new(value, percent));
        Assert.Equal(expected, line.Amount);
        Assert.Equal(allowance, line.DiscountAmount);
    }

    [Theory]
    [InlineData(0.005, false)]
    [InlineData(0.5, true)]
    public void Rounds_the_bill_allowance_to_satang_before_calculating_vat(decimal value, bool percent)
    {
        var invoice = Sample() with { Lines = [new("Item", 1, "EA", 1)], Discount = new(value, percent) };
        Assert.Equal(0.01m, invoice.DiscountAmount);
        Assert.Equal(0.99m, invoice.TaxBasis);
        Assert.Equal(0.07m, invoice.Vat);
        Assert.Equal(1.06m, invoice.GrandTotal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Full_bill_discounts_and_empty_invoices_do_not_divide_by_zero(bool percent)
    {
        var invoice = Sample() with { Discount = new(percent ? 100 : 1500.50m, percent) };
        Assert.Equal(0m, invoice.TaxBasis);
        Assert.Equal(0m, invoice.Vat);
        Assert.Equal(0m, invoice.GrandTotal);
        Assert.All(invoice.LineVats(), vat => Assert.Equal(0m, vat));
        Assert.Empty(invoice.Problems());
        Assert.Empty((invoice with { Lines = [], Discount = new(0, percent) }).LineVats());
        Assert.Equal(0m, (invoice with { Lines = [new("Free", 1, "EA", 0)], Discount = new(100, true) }).Vat);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Zero_discounts_preserve_the_xml_bytes_and_totals(bool includesVat)
    {
        var invoice = Sample() with { PricesIncludeVat = includesVat };
        var created = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.FromHours(7));
        var original = invoice.ToXml(created);
        foreach (var percent in new[] { false, true })
        {
            var zero = invoice with { Discount = new(0, percent), Lines = invoice.Lines.Select(l => l with { Discount = new(0, percent) }).ToArray() };
            Assert.Equal(original, zero.ToXml(created));
            Assert.Equal((invoice.LineTotal, invoice.TaxBasis, invoice.Vat, invoice.GrandTotal), (zero.LineTotal, zero.TaxBasis, zero.Vat, zero.GrandTotal));
            Assert.Equal(invoice.LineVats(), zero.LineVats());
            Assert.Empty(zero.Problems());
        }
        var xml = XDocument.Parse(Encoding.UTF8.GetString(original));
        Assert.Empty(xml.Descendants(Ram + "SpecifiedTradeAllowanceCharge"));
        Assert.Empty(xml.Descendants(Ram + "AllowanceTotalAmount"));
    }

    [Theory]
    [InlineData(true, -1, false, "Discounts cannot be negative.")]
    [InlineData(true, -1, true, "Discounts cannot be negative.")]
    [InlineData(false, -1, false, "Discounts cannot be negative.")]
    [InlineData(false, -1, true, "Discounts cannot be negative.")]
    [InlineData(true, 100.001, false, "The discount cannot exceed the line amount.")]
    [InlineData(false, 100.001, false, "The discount cannot exceed the invoice total.")]
    [InlineData(true, 100.001, true, "A discount percentage cannot exceed 100.")]
    [InlineData(false, 100.001, true, "A discount percentage cannot exceed 100.")]
    public void Reports_each_invalid_discount(bool onLine, decimal value, bool percent, string expected)
    {
        var discount = new TaxDiscount(value, percent);
        var invoice = Sample() with
        {
            Lines = [new("Item", 1, "EA", 100, onLine ? discount : null)],
            Discount = onLine ? null : discount,
        };
        Assert.Equal([expected], invoice.Problems());
    }

    [Fact]
    public void Checks_the_bill_discount_against_the_total_after_line_discounts()
    {
        var invoice = Sample() with { Lines = [new("Item", 1, "EA", 100, new(50))], Discount = new(50.01m) };
        Assert.Equal(["The discount cannot exceed the invoice total."], invoice.Problems());
    }

    private static decimal XmlAmount(XElement element) => decimal.Parse(element.Value, System.Globalization.CultureInfo.InvariantCulture);

    private static void AssertAllowance(XElement allowance, decimal amount)
    {
        Assert.Equal(["ChargeIndicator", "ActualAmount", "TypeCode"], allowance.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("false", allowance.Element(Ram + "ChargeIndicator")!.Value);
        Assert.Equal(amount, XmlAmount(allowance.Element(Ram + "ActualAmount")!));
        Assert.Equal("THB", allowance.Element(Ram + "ActualAmount")!.Attribute("currencyID")!.Value);
        Assert.Equal("95", allowance.Element(Ram + "TypeCode")!.Value);
    }

    [Fact]
    public void Writes_the_seller_address_with_the_codes_the_revenue_department_requires()
    {
        var xml = XDocument.Parse(Encoding.UTF8.GetString(Sample().ToXml(DateTimeOffset.Now)));

        var address = xml.Descendants(Ram + "SellerTradeParty").Single().Element(Ram + "PostalTradeAddress")!;
        Assert.Equal(
            ["PostcodeCode", "LineOne", "LineTwo", "CityName", "CitySubDivisionName", "CountryID", "CountrySubDivisionID", "BuildingNumber"],
            address.Elements().Select(e => e.Name.LocalName)); // The order of the schema.
        Assert.Equal("1017", address.Element(Ram + "CityName")!.Value);
        Assert.Equal("101701", address.Element(Ram + "CitySubDivisionName")!.Value);
        Assert.Equal("10", address.Element(Ram + "CountrySubDivisionID")!.Value);
        Assert.Equal("99/9", address.Element(Ram + "BuildingNumber")!.Value);
    }

    [Theory]
    [InlineData("02-000-0000", "+66-2-000-0000")]
    [InlineData("081 234 5678", "+66-812345678")]
    [InlineData("+66-2-111-2222", "+66-2-111-2222")]
    [InlineData("โทร", null)]
    [InlineData(null, null)]
    public void Writes_the_phone_in_the_form_of_the_schema(string? phone, string? expected) =>
        Assert.Equal(expected, (Sample().Seller with { Phone = phone }).XmlPhone);

    [Fact]
    public void Prints_the_address_with_the_names_of_the_codes()
    {
        var bangkok = Sample().Seller with { Address = "ถนนรัชดาภิเษก" };
        var province = bangkok with { Province = "11", District = "1101", Subdistrict = "110101" };

        Assert.Equal("99/9 ถนนรัชดาภิเษก แขวงห้วยขวาง เขตห้วยขวาง กรุงเทพมหานคร", bangkok.PrintedAddress);
        Assert.Equal("99/9 ถนนรัชดาภิเษก ตำบลปากน้ำ อำเภอเมืองสมุทรปราการ จังหวัดสมุทรปราการ", province.PrintedAddress);
    }

    [Fact]
    public void Knows_the_thai_areas_without_those_no_longer_in_use()
    {
        Assert.Equal(77, ThaiAreas.Provinces.Count);
        Assert.Contains(ThaiAreas.DistrictsOf("10"), a => a.Code == "1017");
        Assert.Contains(ThaiAreas.SubdistrictsOf("1017"), a => a.Code == "101701");
        Assert.DoesNotContain(ThaiAreas.DistrictsOf("25"), a => a.Name.Contains('*'));
    }

    [Fact]
    public void Says_what_is_missing()
    {
        var invoice = Sample() with { Number = " ", Lines = [] };

        var problems = invoice.Problems();

        Assert.Contains("Enter the document number.", problems);
        Assert.Contains("Each item needs a description, a quantity above zero and a price.", problems);
    }

    [Fact]
    public async Task Makes_a_pdf_a3_with_the_xml_inside()
    {
        var invoice = Sample();

        var pdf = Sta.Run(() => App.Imaging.TaxInvoiceRenderer.Render(invoice, DateTimeOffset.Now));

        var path = Path.Combine(_folder.FullName, "invoice.pdf");
        await File.WriteAllBytesAsync(path, pdf);
        await using (var document = await _engine.OpenAsync(path))
        {
            var text = await document.GetPageTextAsync(0);
            Assert.Contains("ใบเสร็จรับเงิน/ใบกำกับภาษี", text, StringComparison.Ordinal);
            Assert.Contains("1,605.54", text, StringComparison.Ordinal);
            Assert.Contains("หนึ่งพันหกร้อยห้าบาทห้าสิบสี่สตางค์", text, StringComparison.Ordinal);
        }

        var cos = CosDocument.Load(pdf);
        var metadata = (CosStream)cos.Resolve(cos.Catalog["Metadata"])!;
        var xmp = XDocument.Parse(Encoding.UTF8.GetString(CosFilters.Decode(metadata, cos.Resolve)).Split("<?xpacket")[1].Split("?>", 2)[1]);
        XNamespace id = "http://www.aiim.org/pdfa/ns/id/";
        XNamespace rsm = "urn:etda:uncefact:data:standard:Invoice_CrossIndustryInvoice:2#";
        Assert.Equal("3", xmp.Descendants(id + "part").Single().Value);
        Assert.Equal("U", xmp.Descendants(id + "conformance").Single().Value); // The workshop of ETDA asks for level U.
        Assert.Equal("ETDA-invoice.xml", xmp.Descendants(rsm + "DocumentFileName").Single().Value);
        Assert.Equal("Tax Invoice", xmp.Descendants(rsm + "DocumentType").Single().Value);
        Assert.Equal("2.0", xmp.Descendants(rsm + "Version").Single().Value);
        Assert.Equal(3, xmp.Descendants(XName.Get("name", "http://www.aiim.org/pdfa/ns/property#")).Count());
        var spec = (CosDictionary)cos.Resolve(((CosArray)cos.Catalog["AF"]!)[0])!;
        Assert.Equal("Source", ((CosName)spec["AFRelationship"]!).Value);
        var file = (CosStream)cos.Resolve(((CosDictionary)spec["EF"]!)["F"])!;
        var xml = XDocument.Parse(Encoding.UTF8.GetString(CosFilters.Decode(file, cos.Resolve)));
        Assert.Equal("1605.54", xml.Descendants(Ram + "GrandTotalAmount").Single().Value);
    }

    [Fact]
    public void Writes_the_structured_buyer_address_in_schema_order()
    {
        var invoice = WithStructuredBuyer();
        var xml = XDocument.Parse(Encoding.UTF8.GetString(invoice.ToXml(DateTimeOffset.Now)));
        var address = xml.Descendants(Ram + "BuyerTradeParty").Single().Element(Ram + "PostalTradeAddress")!;

        Assert.Equal(
            ["PostcodeCode", "LineOne", "LineTwo", "CityName", "CitySubDivisionName", "CountryID", "CountrySubDivisionID", "BuildingNumber"],
            address.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(["10270", "ถนนสุขุมวิท", "ชั้น 2", "1101", "110101", "TH", "11", "12/3"],
            address.Elements().Select(e => e.Value));
        Assert.Empty(invoice.Problems());
        Assert.Equal("12/3 ถนนสุขุมวิท ชั้น 2 ตำบลปากน้ำ อำเภอเมืองสมุทรปราการ จังหวัดสมุทรปราการ", invoice.Buyer.PrintedAddress);
    }

    [Fact]
    public void Keeps_the_free_text_buyer_xml_bytes()
    {
        var invoice = Sample() with { Buyer = Sample().Buyer with { Address = "1 Main Street\nFloor 2", Postcode = "SW1A 1AA" } };
        var bytes = invoice.ToXml(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.FromHours(7)));
        var text = Encoding.UTF8.GetString(bytes);
        var start = text.IndexOf("      <ram:BuyerTradeParty>", StringComparison.Ordinal);
        var end = text.IndexOf("</ram:BuyerTradeParty>", start, StringComparison.Ordinal) + "</ram:BuyerTradeParty>".Length;
        var expected = string.Join(Environment.NewLine,
            "      <ram:BuyerTradeParty>",
            "        <ram:Name>นายสมชาย ใจดี</ram:Name>",
            "        <ram:SpecifiedTaxRegistration>",
            "          <ram:ID schemeID=\"NIDN\">1104599001276</ram:ID>",
            "        </ram:SpecifiedTaxRegistration>",
            "        <ram:PostalTradeAddress>",
            "          <ram:PostcodeCode>SW1A 1AA</ram:PostcodeCode>",
            "          <ram:LineOne>1 Main Street</ram:LineOne>",
            "          <ram:LineTwo>Floor 2</ram:LineTwo>",
            "          <ram:CountryID schemeID=\"3166-1 alpha-2\">TH</ram:CountryID>",
            "        </ram:PostalTradeAddress>",
            "      </ram:BuyerTradeParty>");
        Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(text[start..end]));
        Assert.Empty(invoice.Problems());
        Assert.Equal(invoice.Buyer.Address, invoice.Buyer.PrintedAddress);
    }

    [Theory]
    [InlineData("house")]
    [InlineData("district")]
    [InlineData("subdistrict")]
    public void Says_which_structured_buyer_address_fields_are_required(string missing)
    {
        var invoice = WithStructuredBuyer();
        var buyer = missing switch
        {
            "house" => invoice.Buyer with { BuildingNumber = " " },
            "district" => invoice.Buyer with { District = null },
            _ => invoice.Buyer with { Subdistrict = null },
        };
        Assert.Contains("Enter the buyer's house number, province, district and subdistrict.", (invoice with { Buyer = buyer }).Problems());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("123456")]
    [InlineData("ABCDE")]
    [InlineData("๑๐๒๗๐")]
    public void Requires_five_ascii_digits_for_a_structured_buyer_postcode(string postcode)
    {
        var invoice = WithStructuredBuyer();
        Assert.Contains("The buyer's postcode has 5 digits.", (invoice with { Buyer = invoice.Buyer with { Postcode = postcode } }).Problems());
    }

    [Fact]
    public void A_structured_buyer_does_not_need_an_optional_street()
    {
        var invoice = WithStructuredBuyer();
        Assert.Empty((invoice with { Buyer = invoice.Buyer with { Address = string.Empty } }).Problems());
        Assert.Contains("Enter the buyer's address.", (Sample() with { Buyer = Sample().Buyer with { Address = " " } }).Problems());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("ABCDE")]
    public void Separate_buyer_fields_require_a_province_and_valid_postcode(string postcode)
    {
        var invoice = WithStructuredBuyer();
        var buyer = invoice.Buyer with { Province = null, Postcode = postcode, UsesThaiAddressFields = true };

        Assert.False(buyer.IsStructured);
        var problems = (invoice with { Buyer = buyer }).Problems();
        Assert.Contains("Enter the buyer's house number, province, district and subdistrict.", problems);
        Assert.Contains("The buyer's postcode has 5 digits.", problems);
    }

    [Fact]
    public void Complete_separate_buyer_fields_have_no_problems()
    {
        var invoice = WithStructuredBuyer();
        Assert.Empty((invoice with { Buyer = invoice.Buyer with { UsesThaiAddressFields = true } }).Problems());
    }

    [Theory]
    [InlineData("1 Main Street", "")]
    [InlineData("1 Main Street", "SW1A 1AA")]
    [InlineData(" ", "SW1A 1AA")]
    public void Free_text_buyer_validation_is_unchanged(string address, string postcode)
    {
        var invoice = Sample();
        var buyer = invoice.Buyer with { Address = address, Postcode = postcode, UsesThaiAddressFields = false };
        var problems = (invoice with { Buyer = buyer }).Problems();

        if (string.IsNullOrWhiteSpace(address))
            Assert.Equal(["Enter the buyer's address."], problems);
        else
            Assert.Empty(problems);
    }

    [Fact]
    public void Seller_settings_round_trip_with_or_without_the_address_mode_property()
    {
        var seller = Sample().Seller;
        var path = Path.Combine(_folder.FullName, "etax.json");
        Assert.False(seller.UsesThaiAddressFields);
        new App.Services.ETaxSettings { Seller = seller }.Save(path);
        Assert.Equal(seller, App.Services.ETaxSettings.Load(path).Seller);

        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        Assert.True(json["Seller"]!.AsObject().Remove("UsesThaiAddressFields"));
        File.WriteAllText(path, json.ToJsonString());
        var legacy = App.Services.ETaxSettings.Load(path);
        Assert.Equal(seller, legacy.Seller);
        Assert.False(legacy.Seller!.UsesThaiAddressFields);
        legacy.Save(path);
        Assert.Equal(seller, App.Services.ETaxSettings.Load(path).Seller);
    }

    [Fact]
    public async Task Makes_a_pdf_a3_with_a_structured_buyer()
    {
        var invoice = WithStructuredBuyer();
        var created = DateTimeOffset.Now;
        var pdf = Sta.Run(() => App.Imaging.TaxInvoiceRenderer.Render(invoice, created));
        var path = Path.Combine(_folder.FullName, "structured-buyer.pdf");
        await File.WriteAllBytesAsync(path, pdf);
        await using (var document = await _engine.OpenAsync(path))
        {
            var text = await document.GetPageTextAsync(0);
            Assert.Contains("12/3", text, StringComparison.Ordinal);
            Assert.Contains("ปากน้ำ", text, StringComparison.Ordinal);
            Assert.Contains("สมุทรปราการ", text, StringComparison.Ordinal);
        }
        var cos = CosDocument.Load(pdf);
        var metadata = (CosStream)cos.Resolve(cos.Catalog["Metadata"])!;
        Assert.Contains("<pdfaid:part>3</pdfaid:part>", Encoding.UTF8.GetString(CosFilters.Decode(metadata, cos.Resolve)), StringComparison.Ordinal);
        var spec = (CosDictionary)cos.Resolve(((CosArray)cos.Catalog["AF"]!)[0])!;
        var file = (CosStream)cos.Resolve(((CosDictionary)spec["EF"]!)["F"])!;
        Assert.Equal(invoice.ToXml(created), CosFilters.Decode(file, cos.Resolve));
    }

    private static TaxInvoice WithStructuredBuyer() => Sample() with
    {
        Buyer = Sample().Buyer with
        {
            BuildingNumber = "12/3", Address = "ถนนสุขุมวิท\nชั้น 2", Postcode = "10270",
            Province = "11", District = "1101", Subdistrict = "110101",
        },
    };

    [Fact]
    public void Address_fields_preserve_saved_and_typed_postcodes_and_restore_full_lists()
    {
        Sta.Run(() =>
        {
            // Only in-memory controls: no application startup or window is shown.
            var application = new System.Windows.Application();
            application.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            {
                Source = new Uri("/YrkEasyPdf;component/Themes/Styles.xaml", UriKind.Relative),
            });
            try
            {
                var fields = new App.Views.ThaiAddressFields();
                var province = (System.Windows.Controls.ComboBox)fields.FindName("ProvinceInput");
                var district = (System.Windows.Controls.ComboBox)fields.FindName("DistrictInput");
                var subdistrict = (System.Windows.Controls.ComboBox)fields.FindName("SubdistrictInput");
                var postcode = (System.Windows.Controls.ComboBox)fields.FindName("PostcodeInput");
                fields.Measure(new System.Windows.Size(400, 800));
                fields.Arrange(new System.Windows.Rect(0, 0, 400, 800));
                postcode.ApplyTemplate();
                var editor = (System.Windows.Controls.TextBox)postcode.Template.FindName("PART_EditableTextBox", postcode);

                void Choose(System.Windows.Controls.ComboBox combo, string code) =>
                    combo.SelectedItem = combo.Items.OfType<ThaiArea>().Single(a => a.Code == code);

                var saved = Sample().Seller with { Postcode = "99999" };
                fields.Restore(saved);
                Assert.Equal(saved.Postcode, fields.Postcode);
                Assert.Equal(saved.Subdistrict, fields.Subdistrict);
                Choose(province, "11");
                Assert.Equal("99999", fields.Postcode);

                editor.Text = string.Empty;
                Choose(province, "10");
                Choose(district, "1017");
                Choose(subdistrict, "101701");
                Assert.Equal("10310", fields.Postcode);
                Choose(district, "1018");
                Assert.Equal(string.Empty, fields.Postcode);

                editor.Text = "54321";
                Choose(province, "11");
                Assert.Equal("54321", fields.Postcode);
                Choose(district, "1101");
                Choose(subdistrict, "110101");
                Assert.Equal("54321", fields.Postcode);
                Assert.Equal("110101", fields.Subdistrict);

                fields.Restore(saved with { Province = null, District = null, Subdistrict = null, Postcode = string.Empty });
                editor.Text = "10310";
                var narrowed = ThaiAddressAssist.Narrow("10310");
                Assert.Equal(narrowed.Provinces, province.Items.OfType<ThaiArea>());
                Assert.Equal(narrowed.UniqueProvince?.Code, fields.Province);
                Assert.Equal(narrowed.UniqueDistrict?.Code, fields.District);
                Assert.Equal("10310", fields.Postcode);
                editor.Text = "1031";
                Assert.Equal(77, province.Items.Count);
                // Areas the postcode chose are cleared when the user edits the postcode (plan 1.1.7 step 2).
                Assert.Null(fields.Province);
                Assert.Null(fields.District);

                // Re-picking the province the postcode chose raises no SelectionChanged; closing the list
                // still confirms it, so a later postcode does not replace it (plan 1.1.8 step 3).
                editor.Text = "10200";
                Assert.Equal("10", fields.Province);
                fields.ConfirmAreaChoice();
                editor.Text = "50200";
                Assert.Equal("10", fields.Province);

                fields.Restore(saved with { Postcode = string.Empty, District = "1005", Subdistrict = "100508" });
                // Restoring even an empty postcode does not auto-fill it.
                Assert.Equal(string.Empty, fields.Postcode);
                Assert.Equal(2, postcode.Items.Count);
                postcode.SelectedIndex = 1;
                Assert.Equal("10230", fields.Postcode);

                // The dialog is constructed but never shown. Build must preserve the saved seller exactly.
                var settingsPath = Path.Combine(_folder.FullName, "etax.json");
                new App.Services.ETaxSettings { Seller = saved }.Save(settingsPath);
                var dialog = new App.Views.ETaxInvoiceDialog(App.Services.ETaxSettings.Load(settingsPath));
                TaxInvoice Build() => (TaxInvoice)typeof(App.Views.ETaxInvoiceDialog)
                    .GetMethod("Build", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(dialog, null)!;
                Assert.Equal(saved, Build().Seller);
                var buyerFields = (App.Views.ThaiAddressFields)dialog.FindName("BuyerFields");
                var structured = (System.Windows.Controls.RadioButton)dialog.FindName("BuyerStructured");
                Assert.True(structured.IsChecked);
                Assert.True(Build().Buyer.UsesThaiAddressFields);
                buyerFields.Restore(WithStructuredBuyer().Buyer);
                Assert.Equal("12/3", Build().Buyer.BuildingNumber);
                Assert.Equal("11", Build().Buyer.Province);
                Assert.Equal("1101", Build().Buyer.District);
                Assert.Equal("110101", Build().Buyer.Subdistrict);
                ((System.Windows.Controls.TextBox)dialog.FindName("BuyerAddress")).Text = "Foreign address";
                ((System.Windows.Controls.TextBox)dialog.FindName("BuyerPostcode")).Text = "SW1A 1AA";
                ((System.Windows.Controls.RadioButton)dialog.FindName("BuyerManual")).IsChecked = true;
                Assert.Equal(System.Windows.Visibility.Collapsed, buyerFields.Visibility);
                Assert.Equal(System.Windows.Visibility.Visible, ((System.Windows.FrameworkElement)dialog.FindName("BuyerFreeAddress")).Visibility);
                Assert.Equal("Foreign address", Build().Buyer.Address);
                Assert.Equal("SW1A 1AA", Build().Buyer.Postcode);
                Assert.Null(Build().Buyer.BuildingNumber);
                Assert.False(Build().Buyer.IsStructured);
                Assert.False(Build().Buyer.UsesThaiAddressFields);
                structured.IsChecked = true;
                Assert.Equal("110101", Build().Buyer.Subdistrict);
                return true;
            }
            finally
            {
                application.Shutdown();
            }
        });
    }

    private static TaxInvoice Sample() => new(
        TaxDocumentType.ReceiptTaxInvoice,
        "INV202610-0001",
        new DateTime(2026, 10, 30),
        new TaxParty("บริษัท ตัวอย่าง จำกัด", TaxIdKind.TaxId, "0105536000313", "00001", "99/9 ถนนพระราม 9 แขวงห้วยขวาง เขตห้วยขวาง กรุงเทพฯ\nชั้น 5", "10310", "billing@example.co.th", "02-000-0000", "99/9", "10", "1017", "101701"),
        new TaxParty("นายสมชาย ใจดี", TaxIdKind.NationalId, "1104599001276", string.Empty, "1 ซอยจุฬา 10 ถนนพระราม 6 กรุงเทพฯ", "10330"),
        [new TaxInvoiceLine("กระดาษ A4 80 แกรม", 10, "รีม", 120.05m), new TaxInvoiceLine("ค่าจัดส่ง", 1, string.Empty, 300)]);
}
