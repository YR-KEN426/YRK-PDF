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
                var selectedProvince = fields.Province;
                var selectedDistrict = fields.District;
                editor.Text = "1031";
                Assert.Equal(77, province.Items.Count);
                Assert.Equal(selectedProvince, fields.Province);
                Assert.Equal(selectedDistrict, fields.District);

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
