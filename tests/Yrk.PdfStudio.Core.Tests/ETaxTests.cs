using System.Text;
using System.Xml.Linq;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.ETax;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ETaxTests : IDisposable
{
    private static readonly XNamespace Ram = "urn:etda:uncefact:data:standard:TaxInvoice_ReusableAggregateBusinessInformationEntity:2";
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
    public void Takes_the_vat_out_of_prices_that_include_it()
    {
        var invoice = Sample() with { Lines = [new TaxInvoiceLine("ค่าบริการ", 1, string.Empty, 1070)], PricesIncludeVat = true };

        Assert.Equal(1000m, invoice.TaxBasis);
        Assert.Equal(70m, invoice.Vat);
        Assert.Equal(1070m, invoice.GrandTotal);
        Assert.Empty(invoice.Problems());
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
        Assert.Contains("<pdfaid:part>3</pdfaid:part>", Encoding.UTF8.GetString(CosFilters.Decode(metadata, cos.Resolve)), StringComparison.Ordinal);
        var spec = (CosDictionary)cos.Resolve(((CosArray)cos.Catalog["AF"]!)[0])!;
        Assert.Equal("Source", ((CosName)spec["AFRelationship"]!).Value);
        var file = (CosStream)cos.Resolve(((CosDictionary)spec["EF"]!)["F"])!;
        var xml = XDocument.Parse(Encoding.UTF8.GetString(CosFilters.Decode(file, cos.Resolve)));
        Assert.Equal("1605.54", xml.Descendants(Ram + "GrandTotalAmount").Single().Value);
    }

    private static TaxInvoice Sample() => new(
        TaxDocumentType.ReceiptTaxInvoice,
        "INV202610-0001",
        new DateTime(2026, 10, 30),
        new TaxParty("บริษัท ตัวอย่าง จำกัด", TaxIdKind.TaxId, "0105536000313", "00001", "99/9 ถนนพระราม 9 แขวงห้วยขวาง เขตห้วยขวาง กรุงเทพฯ\nชั้น 5", "10310", "billing@example.co.th", "02-000-0000"),
        new TaxParty("นายสมชาย ใจดี", TaxIdKind.NationalId, "1104599001276", string.Empty, "1 ซอยจุฬา 10 ถนนพระราม 6 กรุงเทพฯ", "10330"),
        [new TaxInvoiceLine("กระดาษ A4 80 แกรม", 10, "รีม", 120.05m), new TaxInvoiceLine("ค่าจัดส่ง", 1, string.Empty, 300)]);
}
