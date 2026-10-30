using System.Globalization;
using System.Text;
using System.Xml;
using Yrk.PdfStudio.Core.Pdf.Archive;

namespace Yrk.PdfStudio.Core.Pdf.ETax;

/// <summary>The document types of an e-Tax Invoice (RD/ETDA 1001 Thai Document Name Code, ขมธอ. 3-2560 appendix B.2).</summary>
public enum TaxDocumentType
{
    TaxInvoice,           // 388 ใบกำกับภาษี
    InvoiceTaxInvoice,    // T02 ใบแจ้งหนี้/ใบกำกับภาษี
    ReceiptTaxInvoice,    // T03 ใบเสร็จรับเงิน/ใบกำกับภาษี
    DeliveryTaxInvoice,   // T04 ใบส่งของ/ใบกำกับภาษี
}

/// <summary>How a party is identified for tax (appendix B.16).</summary>
public enum TaxIdKind
{
    /// <summary>A 13-digit tax ID and a 5-digit branch ("00000" for the head office).</summary>
    TaxId,

    /// <summary>A Thai person's 13-digit ID card number.</summary>
    NationalId,

    /// <summary>A foreigner's passport number.</summary>
    Passport,

    /// <summary>The buyer gave no number.</summary>
    None,
}

/// <param name="Branch">Five digits; "00000" is the head office. Only for <see cref="TaxIdKind.TaxId"/>.</param>
/// <param name="Address">The address as written (one or two lines). With a province it is only the part besides the codes: street, soi, building.</param>
/// <param name="BuildingNumber">The house number; the seller must have one.</param>
/// <param name="Province">The province, district and subdistrict codes (<see cref="ThaiAreas"/>); the Revenue Department requires them of the seller.</param>
public sealed record TaxParty(
    string Name,
    TaxIdKind IdKind,
    string Id,
    string Branch,
    string Address,
    string Postcode,
    string? Email = null,
    string? Phone = null,
    string? BuildingNumber = null,
    string? Province = null,
    string? District = null,
    string? Subdistrict = null)
{
    /// <summary>The address gives its province, district and subdistrict as codes.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsStructured => !string.IsNullOrEmpty(Province);

    /// <summary>The address as printed on the invoice, one or more lines, without the postcode.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PrintedAddress
    {
        get
        {
            if (!IsStructured)
            {
                return Address;
            }

            // Bangkok has khwaeng and khet, and its code list names already start with "เขต".
            var bangkok = Province == "10";
            var parts = new[]
            {
                BuildingNumber?.Trim(),
                Address.Replace('\n', ' ').Trim(),
                (bangkok ? "แขวง" : "ตำบล") + ThaiAreas.Name(Subdistrict),
                (bangkok ? string.Empty : "อำเภอ") + ThaiAreas.Name(District),
                (bangkok ? string.Empty : "จังหวัด") + ThaiAreas.Name(Province),
            };
            return string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p)));
        }
    }

    /// <summary>The phone number in the form the schema allows: +66-2-000-0000; null when it has no digits.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? XmlPhone
    {
        get
        {
            var text = Phone?.Trim() ?? string.Empty;
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\+[0-9]{1,3}-[0-9()+\-]{1,30}$"))
            {
                return text;
            }

            var kept = new string(text.Where(c => char.IsAsciiDigit(c) || c == '-').ToArray()).Trim('-');
            var digits = kept.Replace("-", string.Empty, StringComparison.Ordinal);
            if (digits.Length == 0)
            {
                return null;
            }

            return "+66-" + (kept[0] == '0' ? kept[1..].TrimStart('-') : kept);
        }
    }

    /// <summary>The number as the XML carries it: tax ID followed by the branch, or the other identification.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string XmlId => IdKind switch
    {
        TaxIdKind.TaxId => Digits(Id) + Digits(Branch).PadLeft(5, '0'),
        TaxIdKind.NationalId => Digits(Id),
        TaxIdKind.None => "N/A",
        _ => Id.Trim(),
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string SchemeId => IdKind switch
    {
        TaxIdKind.TaxId => "TXID",
        TaxIdKind.NationalId => "NIDN",
        TaxIdKind.Passport => "CCPT",
        _ => "OTHR",
    };

    private static string Digits(string text) => new(text.Where(char.IsAsciiDigit).ToArray());
}

/// <param name="Unit">The unit as written (for example "ชิ้น", "EA"); empty for a service.</param>
public sealed record TaxInvoiceLine(string Description, decimal Quantity, string Unit, decimal UnitPrice)
{
    public decimal Amount => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
}

/// <summary>An e-Tax Invoice: what is printed on the PDF and written in its XML (ขมธอ. 3-2560 version 2.0).</summary>
/// <param name="VatRate">In percent (7 in Thailand today; 0 for zero-rated sales).</param>
/// <param name="PricesIncludeVat">The unit prices already include VAT (the VAT is then taken out of the total).</param>
public sealed record TaxInvoice(
    TaxDocumentType Type,
    string Number,
    DateTime IssueDate,
    TaxParty Seller,
    TaxParty Buyer,
    IReadOnlyList<TaxInvoiceLine> Lines,
    decimal VatRate = 7,
    bool PricesIncludeVat = false,
    string? Note = null)
{
    /// <summary>The name ETDA gives the XML inside the PDF.</summary>
    public const string XmlFileName = "ETDA-invoice.xml";

    /// <summary>The metadata ETDA asks of the PDF/A-3 file (its PdfAConverter template).</summary>
    public static PdfAExtension XmpExtension { get; } = new(
        "Electronic Tax Invoice PDFA Extension Schema",
        "urn:etda:uncefact:data:standard:Invoice_CrossIndustryInvoice:2#",
        "rsm",
        [
            new PdfAExtensionProperty("DocumentFileName", XmlFileName, "Name of the embedded XML invoice file"),
            new PdfAExtensionProperty("DocumentType", "Tax Invoice", "Type of the document"),
            new PdfAExtensionProperty("Version", "2.0", "Version of the ETDA XML data"),
        ]);

    public decimal LineTotal => Lines.Sum(l => l.Amount);

    /// <summary>The value before VAT.</summary>
    public decimal TaxBasis => PricesIncludeVat ? LineTotal - Vat : LineTotal;

    public decimal Vat => PricesIncludeVat
        ? Math.Round(LineTotal * VatRate / (100 + VatRate), 2, MidpointRounding.AwayFromZero)
        : Math.Round(LineTotal * VatRate / 100, 2, MidpointRounding.AwayFromZero);

    public decimal GrandTotal => TaxBasis + Vat;

    public string TypeCode => Type switch
    {
        TaxDocumentType.InvoiceTaxInvoice => "T02",
        TaxDocumentType.ReceiptTaxInvoice => "T03",
        TaxDocumentType.DeliveryTaxInvoice => "T04",
        _ => "388",
    };

    /// <summary>The document name in Thai, as printed and as in the XML.</summary>
    public string ThaiName => Type switch
    {
        TaxDocumentType.InvoiceTaxInvoice => "ใบแจ้งหนี้/ใบกำกับภาษี",
        TaxDocumentType.ReceiptTaxInvoice => "ใบเสร็จรับเงิน/ใบกำกับภาษี",
        TaxDocumentType.DeliveryTaxInvoice => "ใบส่งของ/ใบกำกับภาษี",
        _ => "ใบกำกับภาษี",
    };

    /// <summary>What must be right before the invoice can be issued (empty when it is complete), as English templates.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Number))
        {
            problems.Add("Enter the document number.");
        }

        if (string.IsNullOrWhiteSpace(Seller.Name) || Seller.IdKind != TaxIdKind.TaxId || !IsValidThaiId(Seller.Id))
        {
            problems.Add("Enter the seller's name and a valid 13-digit tax ID.");
        }

        if (Seller.Branch.Count(char.IsAsciiDigit) != 5)
        {
            problems.Add("The branch number has 5 digits (00000 for the head office).");
        }

        if (string.IsNullOrWhiteSpace(Seller.BuildingNumber) || string.IsNullOrEmpty(Seller.Province)
            || string.IsNullOrEmpty(Seller.District) || string.IsNullOrEmpty(Seller.Subdistrict))
        {
            problems.Add("Enter the seller's house number, province, district and subdistrict.");
        }

        if (Seller.Postcode.Trim() is not { Length: 5 } postcode || !postcode.All(char.IsAsciiDigit))
        {
            problems.Add("The seller's postcode has 5 digits.");
        }

        if (string.IsNullOrWhiteSpace(Buyer.Name))
        {
            problems.Add("Enter the buyer's name.");
        }

        if (string.IsNullOrWhiteSpace(Buyer.Address))
        {
            problems.Add("Enter the buyer's address.");
        }

        if (Buyer.IdKind is TaxIdKind.TaxId or TaxIdKind.NationalId && !IsValidThaiId(Buyer.Id))
        {
            problems.Add("The buyer's 13-digit number is not valid.");
        }

        if (Buyer.IdKind == TaxIdKind.TaxId && Buyer.Branch.Count(char.IsAsciiDigit) != 5)
        {
            problems.Add("The branch number has 5 digits (00000 for the head office).");
        }

        if (Lines.Count == 0 || Lines.Any(l => string.IsNullOrWhiteSpace(l.Description) || l.Quantity <= 0 || l.UnitPrice < 0))
        {
            problems.Add("Each item needs a description, a quantity above zero and a price.");
        }

        return problems.Distinct().ToList();
    }

    /// <summary>A Thai tax ID or ID card number: 13 digits whose last one is the check digit.</summary>
    public static bool IsValidThaiId(string text)
    {
        var digits = text.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length != 13 || text.Count(char.IsLetterOrDigit) != 13)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            sum += digits[i] * (13 - i);
        }

        return (11 - (sum % 11)) % 10 == digits[12];
    }

    /// <summary>The XML of the e-Tax Invoice (TaxInvoice_CrossIndustryInvoice, ขมธอ. 3-2560 version 2.0), UTF-8.</summary>
    /// <param name="created">When the document is made (CreationDateTime).</param>
    public byte[] ToXml(DateTimeOffset created)
    {
        const string Rsm = "urn:etda:uncefact:data:standard:TaxInvoice_CrossIndustryInvoice:2";
        const string Ram = "urn:etda:uncefact:data:standard:TaxInvoice_ReusableAggregateBusinessInformationEntity:2";
        using var output = new MemoryStream();
        using (var xml = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            void Element(string name, string value, params (string Name, string Value)[] attributes)
            {
                xml.WriteStartElement("ram", name, Ram);
                foreach (var (attribute, text) in attributes)
                {
                    xml.WriteAttributeString(attribute, text);
                }

                xml.WriteString(value);
                xml.WriteEndElement();
            }

            void Start(string name) => xml.WriteStartElement("ram", name, Ram);
            void End() => xml.WriteEndElement();

            void Party(string name, TaxParty party)
            {
                Start(name);
                Element("Name", party.Name.Trim());
                Start("SpecifiedTaxRegistration");
                Element("ID", party.XmlId, ("schemeID", party.SchemeId));
                End();
                if (!string.IsNullOrWhiteSpace(party.Email) || party.XmlPhone is not null)
                {
                    Start("DefinedTradeContact");
                    if (!string.IsNullOrWhiteSpace(party.Email))
                    {
                        Start("EmailURIUniversalCommunication");
                        Element("URIID", party.Email.Trim());
                        End();
                    }

                    if (party.XmlPhone is { } phone)
                    {
                        Start("TelephoneUniversalCommunication");
                        Element("CompleteNumber", phone);
                        End();
                    }

                    End();
                }

                // Lines 1–2 hold what was written (§8.7); the seller also needs the codes (Revenue Department rules 009–012).
                Start("PostalTradeAddress");
                if (party.Postcode.Trim().Length > 0)
                {
                    Element("PostcodeCode", party.Postcode.Trim());
                }

                var lines = AddressLines(party.Address);
                if (lines.One.Length > 0)
                {
                    Element("LineOne", lines.One);
                }

                if (lines.Two.Length > 0)
                {
                    Element("LineTwo", lines.Two);
                }

                if (party.IsStructured)
                {
                    Element("CityName", party.District!);
                    Element("CitySubDivisionName", party.Subdistrict!);
                }

                Element("CountryID", "TH", ("schemeID", "3166-1 alpha-2"));
                if (party.IsStructured)
                {
                    Element("CountrySubDivisionID", party.Province!);
                    Element("BuildingNumber", party.BuildingNumber!.Trim());
                }

                End();
                End();
            }

            void Tax(decimal basis, decimal amount)
            {
                Start("ApplicableTradeTax");
                Element("TypeCode", "VAT");
                Element("CalculatedRate", Amount(VatRate));
                Element("BasisAmount", Amount(basis));
                Element("CalculatedAmount", Amount(amount));
                End();
            }

            xml.WriteStartDocument();
            xml.WriteStartElement("rsm", "TaxInvoice_CrossIndustryInvoice", Rsm);
            xml.WriteAttributeString("xmlns", "ram", null, Ram);

            xml.WriteStartElement("rsm", "ExchangedDocumentContext", Rsm);
            Start("GuidelineSpecifiedDocumentContextParameter");
            Element("ID", "ER3-2560", ("schemeAgencyID", "ETDA"), ("schemeVersionID", "v2.0"));
            End();
            xml.WriteEndElement();

            xml.WriteStartElement("rsm", "ExchangedDocument", Rsm);
            Element("ID", Number.Trim());
            Element("Name", ThaiName);
            Element("TypeCode", TypeCode);
            Element("IssueDateTime", IssueDate.Date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
            Element("CreationDateTime", created.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(Note))
            {
                Start("IncludedNote");
                Element("Subject", "หมายเหตุ");
                Element("Content", Note.Trim());
                End();
            }

            xml.WriteEndElement();

            xml.WriteStartElement("rsm", "SupplyChainTradeTransaction", Rsm);
            Start("ApplicableHeaderTradeAgreement");
            Party("SellerTradeParty", Seller);
            Party("BuyerTradeParty", Buyer);
            End();
            Start("ApplicableHeaderTradeDelivery");
            End();
            Start("ApplicableHeaderTradeSettlement");
            Element("InvoiceCurrencyCode", "THB", ("listID", "ISO 4217 3A"));
            Tax(TaxBasis, Vat);
            Start("SpecifiedTradeSettlementHeaderMonetarySummation");
            Element("LineTotalAmount", Amount(LineTotal));
            Element("TaxBasisTotalAmount", Amount(TaxBasis));
            Element("TaxTotalAmount", Amount(Vat));
            Element("GrandTotalAmount", Amount(GrandTotal));
            End();
            End();

            var lineVats = LineVats();
            for (var i = 0; i < Lines.Count; i++)
            {
                var line = Lines[i];
                var lineVat = lineVats[i];
                var lineBasis = PricesIncludeVat ? line.Amount - lineVat : line.Amount;
                Start("IncludedSupplyChainTradeLineItem");
                Start("AssociatedDocumentLineDocument");
                Element("LineID", (i + 1).ToString(CultureInfo.InvariantCulture));
                End();
                Start("SpecifiedTradeProduct");
                Element("Name", line.Description.Trim());
                End();
                Start("SpecifiedLineTradeAgreement");
                Start("GrossPriceProductTradePrice");
                Element("ChargeAmount", Amount(line.UnitPrice));
                End();
                End();
                Start("SpecifiedLineTradeDelivery");
                // The schema always asks for the quantity; a service has no unit (§8.18).
                Element("BilledQuantity", Amount(line.Quantity), line.Unit.Trim() is { Length: > 0 } unit ? [("unitCode", unit)] : []);
                End();
                Start("SpecifiedLineTradeSettlement");
                Tax(lineBasis, lineVat);
                Start("SpecifiedTradeSettlementLineMonetarySummation");
                Element("TaxTotalAmount", Amount(lineVat));
                Element("NetLineTotalAmount", Amount(lineBasis), ("currencyID", "THB"));
                Element("NetIncludingTaxesLineTotalAmount", Amount(lineBasis + lineVat), ("currencyID", "THB"));
                End();
                End();
                End();
            }

            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return output.ToArray();
    }

    /// <summary>The VAT of each line. Rounded line by line they can miss the VAT of the whole by a satang or so, which the last line takes up.</summary>
    internal IReadOnlyList<decimal> LineVats()
    {
        var vats = Lines.Select(line => Math.Round(PricesIncludeVat ? line.Amount * VatRate / (100 + VatRate) : line.Amount * VatRate / 100, 2, MidpointRounding.AwayFromZero)).ToList();
        if (vats.Count > 0)
        {
            vats[^1] += Vat - vats.Sum();
        }

        return vats;
    }

    private static string Amount(decimal value) => value.ToString("0.00####", CultureInfo.InvariantCulture);

    /// <summary>Up to two lines of at most 256 characters: the written line breaks, or the whole address on line one.</summary>
    internal static (string One, string Two) AddressLines(string address)
    {
        var lines = address.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var one = lines.Length > 0 ? lines[0] : string.Empty;
        var two = string.Join(" ", lines.Skip(1));
        return (Cut(one), Cut(two));

        static string Cut(string text) => text.Length <= 256 ? text : text[..256];
    }
}
