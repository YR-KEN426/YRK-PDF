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
    DebitNote,            // 80 ใบเพิ่มหนี้
    CreditNote,           // 81 ใบลดหนี้
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
/// <param name="UsesThaiAddressFields">The user chose separate Thai address fields; the structured checks apply even if fields are empty.</param>
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
    string? Subdistrict = null,
    bool UsesThaiAddressFields = false)
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

/// <summary>A discount in baht, or as a percentage of the amount being discounted.</summary>
public sealed record TaxDiscount(decimal Value, bool IsPercent = false)
{
    internal decimal Of(decimal amount) => IsPercent ? amount * Value / 100 : Value;
}

/// <param name="Unit">The unit as written (for example "ชิ้น", "EA"); empty for a service.</param>
/// <param name="Discount">A discount on the whole line, before VAT is calculated.</param>
public sealed record TaxInvoiceLine(string Description, decimal Quantity, string Unit, decimal UnitPrice, TaxDiscount? Discount = null)
{
    public decimal Gross => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);

    public decimal DiscountAmount => Math.Round(Discount?.Of(Gross) ?? 0, 2, MidpointRounding.AwayFromZero);

    public decimal Amount => Gross - DiscountAmount;
}

/// <summary>The original tax invoice adjusted by a debit or credit note; its value excludes VAT.</summary>
public sealed record TaxInvoiceReference(string Number, DateTime IssueDate, TaxDocumentType Type, decimal OriginalValue);

/// <summary>An ETDA purpose code and optional explanation (required for codes ending in 99).</summary>
public sealed record TaxInvoiceReason(string Code, string? Text = null);

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
    string? Note = null,
    TaxDiscount? Discount = null,
    TaxInvoiceReference? Reference = null,
    TaxInvoiceReason? Reason = null)
{
    /// <summary>The name ETDA gives the XML inside the PDF.</summary>
    public const string XmlFileName = "ETDA-invoice.xml";

    private static readonly PdfAExtension InvoiceXmpExtension = new(
        "Electronic Tax Invoice PDFA Extension Schema",
        "urn:etda:uncefact:data:standard:Invoice_CrossIndustryInvoice:2#",
        "rsm",
        [
            new PdfAExtensionProperty("DocumentFileName", XmlFileName, "Name of the embedded XML invoice file"),
            new PdfAExtensionProperty("DocumentType", "Tax Invoice", "Type of the document"),
            new PdfAExtensionProperty("Version", "2.0", "Version of the ETDA XML data"),
        ]);

    /// <summary>The metadata ETDA asks of the PDF/A-3 file, with this document's type.</summary>
    public PdfAExtension XmpExtension => IsDebitCreditNote
        ? InvoiceXmpExtension with
        {
            Properties = InvoiceXmpExtension.Properties.Select(p => p.Name == "DocumentType"
                ? p with { Value = Type == TaxDocumentType.DebitNote ? "Debit Note" : "Credit Note" }
                : p).ToArray(),
        }
        : InvoiceXmpExtension;

    public bool IsDebitCreditNote => Type is TaxDocumentType.DebitNote or TaxDocumentType.CreditNote;

    /// <summary>The referenced invoice's value before VAT (only for debit and credit notes).</summary>
    public decimal OriginalValue => Reference?.OriginalValue ?? 0;

    /// <summary>The adjustment after all discounts, before VAT.</summary>
    public decimal Difference => TaxBasis;

    /// <summary>The corrected invoice value before VAT (only for debit and credit notes).</summary>
    public decimal CorrectValue => Type == TaxDocumentType.CreditNote ? OriginalValue - Difference : OriginalValue + Difference;

    public decimal LineTotal => Lines.Sum(l => l.Amount);

    /// <summary>The bill-level allowance only, in the same pricing mode as the lines.</summary>
    public decimal DiscountAmount => Math.Round(Discount?.Of(LineTotal) ?? 0, 2, MidpointRounding.AwayFromZero);

    private decimal DiscountedTotal => LineTotal - DiscountAmount;

    /// <summary>The value before VAT.</summary>
    public decimal TaxBasis => PricesIncludeVat ? DiscountedTotal - Vat : DiscountedTotal;

    public decimal Vat => PricesIncludeVat
        ? Math.Round(DiscountedTotal * VatRate / (100 + VatRate), 2, MidpointRounding.AwayFromZero)
        : Math.Round(DiscountedTotal * VatRate / 100, 2, MidpointRounding.AwayFromZero);

    public decimal GrandTotal => TaxBasis + Vat;

    public string TypeCode => Type switch
    {
        TaxDocumentType.InvoiceTaxInvoice => "T02",
        TaxDocumentType.ReceiptTaxInvoice => "T03",
        TaxDocumentType.DeliveryTaxInvoice => "T04",
        TaxDocumentType.DebitNote => "80",
        TaxDocumentType.CreditNote => "81",
        _ => "388",
    };

    /// <summary>The document name in Thai, as printed and as in the XML.</summary>
    public string ThaiName => Type switch
    {
        TaxDocumentType.InvoiceTaxInvoice => "ใบแจ้งหนี้/ใบกำกับภาษี",
        TaxDocumentType.ReceiptTaxInvoice => "ใบเสร็จรับเงิน/ใบกำกับภาษี",
        TaxDocumentType.DeliveryTaxInvoice => "ใบส่งของ/ใบกำกับภาษี",
        TaxDocumentType.DebitNote => "ใบเพิ่มหนี้",
        TaxDocumentType.CreditNote => "ใบลดหนี้",
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

        if (Buyer.IsStructured || Buyer.UsesThaiAddressFields)
        {
            if (string.IsNullOrWhiteSpace(Buyer.BuildingNumber) || string.IsNullOrEmpty(Buyer.Province)
                || string.IsNullOrEmpty(Buyer.District) || string.IsNullOrEmpty(Buyer.Subdistrict))
            {
                problems.Add("Enter the buyer's house number, province, district and subdistrict.");
            }

            if (Buyer.Postcode.Trim() is not { Length: 5 } buyerPostcode || !buyerPostcode.All(char.IsAsciiDigit))
            {
                problems.Add("The buyer's postcode has 5 digits.");
            }
        }
        else if (string.IsNullOrWhiteSpace(Buyer.Address))
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

        foreach (var line in Lines)
        {
            CheckDiscount(line.Discount, line.Gross, "line amount");
        }

        CheckDiscount(Discount, LineTotal, "invoice total");

        if (IsDebitCreditNote)
        {
            if (Reference is null || string.IsNullOrWhiteSpace(Reference.Number))
            {
                problems.Add("Enter the original invoice number.");
            }

            if (Reference is null || Reference.IssueDate == default)
            {
                problems.Add("Enter the original invoice date.");
            }
            else if (Reference.IssueDate.Date > IssueDate.Date)
            {
                problems.Add("The original invoice date is after this document's date.");
            }

            if (Reference is null || Reference.OriginalValue <= 0)
            {
                problems.Add("Enter the original invoice value before VAT, above zero.");
            }

            if (Reference is not null && Reference.Type is not (TaxDocumentType.TaxInvoice or TaxDocumentType.InvoiceTaxInvoice
                or TaxDocumentType.ReceiptTaxInvoice or TaxDocumentType.DeliveryTaxInvoice))
            {
                problems.Add("The original document must be a tax invoice (388, T02, T03 or T04).");
            }

            if (Type == TaxDocumentType.CreditNote && Reference is not null && Difference > OriginalValue)
            {
                problems.Add("The credit note cannot reduce more than the original invoice value.");
            }

            if (Reason is null || string.IsNullOrWhiteSpace(Reason.Code))
            {
                problems.Add("Enter the debit or credit note reason code.");
            }
            else if (!IsAllowedReasonCode(Reason.Code))
            {
                problems.Add("The reason code is not valid for this document type.");
            }
            else if (Reason.Code.EndsWith("99", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(Reason.Text))
            {
                problems.Add("Enter the reason text for a code ending in 99.");
            }
        }
        else
        {
            if (Reference is not null)
            {
                problems.Add("Only debit and credit notes can reference an original invoice.");
            }

            if (Reason is not null)
            {
                problems.Add("Only debit and credit notes can have a reason.");
            }
        }

        return problems.Distinct().ToList();

        void CheckDiscount(TaxDiscount? discount, decimal amount, string scope)
        {
            if (discount is null)
            {
                return;
            }

            if (discount.Value < 0)
            {
                problems.Add("Discounts cannot be negative.");
            }
            else if (discount.IsPercent && discount.Value > 100)
            {
                problems.Add("A discount percentage cannot exceed 100.");
            }
            else if (discount.Value > 0 && discount.Of(amount) > amount)
            {
                problems.Add($"The discount cannot exceed the {scope}.");
            }
        }
    }

    private bool IsAllowedReasonCode(string code) => Type switch
    {
        TaxDocumentType.DebitNote => code is "DBNG01" or "DBNG02" or "DBNG99" or "DBNS01" or "DBNS02" or "DBNS99",
        TaxDocumentType.CreditNote => code is "CDNG01" or "CDNG02" or "CDNG03" or "CDNG04" or "CDNG05" or "CDNG99"
            or "CDNS01" or "CDNS02" or "CDNS03" or "CDNS04" or "CDNS99",
        _ => false,
    };

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

    /// <summary>The XML of the invoice or debit/credit note (ขมธอ. 3-2560 version 2.0), UTF-8.</summary>
    /// <param name="created">When the document is made (CreationDateTime).</param>
    public byte[] ToXml(DateTimeOffset created)
    {
        var lineVats = LineVats();
        var lineBases = XmlLineBases(lineVats);
        var lineTotal = lineBases.Sum();
        var allowance = XmlAllowanceAmount(lineTotal);
        var documentKind = IsDebitCreditNote ? "DebitCreditNote" : "TaxInvoice";
        var Rsm = $"urn:etda:uncefact:data:standard:{documentKind}_CrossIndustryInvoice:2";
        var Ram = $"urn:etda:uncefact:data:standard:{documentKind}_ReusableAggregateBusinessInformationEntity:2";
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

            void Allowance(decimal amount)
            {
                Start("SpecifiedTradeAllowanceCharge");
                Element("ChargeIndicator", "false");
                Element("ActualAmount", Amount(amount), ("currencyID", "THB"));
                Element("TypeCode", "95");
                End();
            }

            xml.WriteStartDocument();
            xml.WriteStartElement("rsm", $"{documentKind}_CrossIndustryInvoice", Rsm);
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
            if (IsDebitCreditNote && Reason is not null)
            {
                if (!string.IsNullOrWhiteSpace(Reason.Text))
                {
                    Element("Purpose", Reason.Text.Trim());
                }

                Element("PurposeCode", Reason.Code);
            }

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
            if (IsDebitCreditNote && Reference is not null)
            {
                Start("AdditionalReferencedDocument");
                Element("IssuerAssignedID", Reference.Number.Trim());
                Element("IssueDateTime", Reference.IssueDate.Date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
                Element("ReferenceTypeCode", Reference.Type switch
                {
                    TaxDocumentType.TaxInvoice => "388",
                    TaxDocumentType.InvoiceTaxInvoice => "T02",
                    TaxDocumentType.ReceiptTaxInvoice => "T03",
                    TaxDocumentType.DeliveryTaxInvoice => "T04",
                    _ => throw new InvalidOperationException("The referenced document must be a tax invoice."),
                });
                End();
            }

            End();
            Start("ApplicableHeaderTradeDelivery");
            End();
            Start("ApplicableHeaderTradeSettlement");
            Element("InvoiceCurrencyCode", "THB", ("listID", "ISO 4217 3A"));
            Tax(TaxBasis, Vat);
            if (DiscountAmount != 0)
            {
                Allowance(allowance);
            }
            Start("SpecifiedTradeSettlementHeaderMonetarySummation");
            if (IsDebitCreditNote)
            {
                Element("OriginalInformationAmount", Amount(OriginalValue));
            }

            Element("LineTotalAmount", Amount(IsDebitCreditNote ? CorrectValue : lineTotal));
            if (IsDebitCreditNote)
            {
                Element("DifferenceInformationAmount", Amount(Difference));
            }

            if (DiscountAmount != 0)
            {
                Element("AllowanceTotalAmount", Amount(allowance));
            }

            Element("TaxBasisTotalAmount", Amount(TaxBasis));
            Element("TaxTotalAmount", Amount(Vat));
            Element("GrandTotalAmount", Amount(GrandTotal));
            End();
            End();

            for (var i = 0; i < Lines.Count; i++)
            {
                var line = Lines[i];
                var lineVat = lineVats[i];
                var lineBasis = lineBases[i];
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
                if (line.DiscountAmount != 0)
                {
                    Allowance(XmlLineAllowanceAmount(line, lineBasis));
                }
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

    // XML amounts exclude VAT; the public totals retain the pricing mode used by the PDF.
    internal IReadOnlyList<decimal> XmlLineBases(IReadOnlyList<decimal> lineVats) =>
        Lines.Select((line, i) => PricesIncludeVat ? line.Amount - lineVats[i] : line.Amount).ToArray();

    internal decimal XmlAllowanceAmount(decimal lineTotal) => PricesIncludeVat ? lineTotal - TaxBasis : DiscountAmount;

    internal decimal XmlLineAllowanceAmount(TaxInvoiceLine line, decimal lineBasis) => PricesIncludeVat
        ? line.Gross - Math.Round(line.Gross * VatRate / (100 + VatRate), 2, MidpointRounding.AwayFromZero) - lineBasis
        : line.DiscountAmount;

    /// <summary>The VAT after each line's own discount; reconciled to the invoice VAT only without a bill allowance.</summary>
    internal IReadOnlyList<decimal> LineVats()
    {
        var vats = Lines.Select(line =>
        {
            var amount = line.Amount;
            return Math.Round(PricesIncludeVat ? amount * VatRate / (100 + VatRate) : amount * VatRate / 100, 2, MidpointRounding.AwayFromZero);
        }).ToList();
        if (vats.Count > 0 && DiscountAmount == 0)
        {
            // A free line cannot absorb a negative remainder; prefer the later line on ties.
            var largest = 0;
            for (var i = 1; i < Lines.Count; i++)
            {
                if (Lines[i].Amount >= Lines[largest].Amount) largest = i;
            }
            vats[largest] += Vat - vats.Sum();
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
