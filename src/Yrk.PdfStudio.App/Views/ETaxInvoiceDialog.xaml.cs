using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf.ETax;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.App.Views;

/// <summary>Fills in an e-Tax Invoice: the seller (remembered), the buyer, the items and VAT, with the totals as you type.</summary>
public partial class ETaxInvoiceDialog : Window
{
    private static readonly (TaxDocumentType Type, string Name)[] Types =
    [
        (TaxDocumentType.TaxInvoice, "ใบกำกับภาษี (388)"),
        (TaxDocumentType.InvoiceTaxInvoice, "ใบแจ้งหนี้/ใบกำกับภาษี (T02)"),
        (TaxDocumentType.ReceiptTaxInvoice, "ใบเสร็จรับเงิน/ใบกำกับภาษี (T03)"),
        (TaxDocumentType.DeliveryTaxInvoice, "ใบส่งของ/ใบกำกับภาษี (T04)"),
        (TaxDocumentType.DebitNote, "ใบเพิ่มหนี้ (80)"),
        (TaxDocumentType.CreditNote, "ใบลดหนี้ (81)"),
    ];

    // คำไทยแปลจากฉบับอังกฤษของสเปก ต้องเทียบฉบับไทย (Ken ตรวจ)
    private static readonly (string Code, string ThaiText)[] Reasons =
    [
        ("DBNG01", "ราคาสินค้าเพิ่ม (สินค้าเกินจำนวนที่ตกลง)"),
        ("DBNG02", "คำนวณราคาสินค้าผิดพลาดต่ำกว่าที่เป็นจริง"),
        ("DBNG99", "เหตุอื่น (ระบุ)"),
        ("DBNS01", "ค่าบริการเพิ่ม (บริการเกินข้อกำหนดที่ตกลง)"),
        ("DBNS02", "คำนวณค่าบริการผิดพลาดต่ำกว่าที่เป็นจริง"),
        ("DBNS99", "เหตุอื่น (ระบุ)"),
        ("CDNG01", "ลดราคาสินค้าที่ขาย (สินค้าผิดข้อกำหนดที่ตกลง)"),
        ("CDNG02", "สินค้าชำรุดเสียหาย"),
        ("CDNG03", "สินค้าขาดจำนวนตามที่ตกลง"),
        ("CDNG04", "คำนวณราคาสินค้าผิดพลาดสูงกว่าที่เป็นจริง"),
        ("CDNG05", "รับคืนสินค้า (ไม่ตรงตามคำพรรณนา)"),
        ("CDNG99", "เหตุอื่น (ระบุ)"),
        ("CDNS01", "ลดค่าบริการ (บริการผิดข้อกำหนดที่ตกลง)"),
        ("CDNS02", "ค่าบริการขาดจำนวน"),
        ("CDNS03", "คำนวณค่าบริการผิดพลาดสูงกว่าที่เป็นจริง"),
        ("CDNS04", "บอกเลิกสัญญาบริการ"),
        ("CDNS99", "เหตุอื่น (ระบุ)"),
    ];

    private static readonly (TaxIdKind Kind, string Name)[] Kinds =
    [
        (TaxIdKind.TaxId, "Tax ID"),
        (TaxIdKind.NationalId, "ID card"),
        (TaxIdKind.Passport, "Passport"),
        (TaxIdKind.None, "No number"),
    ];

    private readonly ObservableCollection<ItemRow> _items = [];
    private bool _ready;
    private readonly ETaxSettings _settings;
    private string _suggestedNumber = string.Empty;
    private (string Code, string ThaiText)[] _reasons = [];
    private TaxDocumentType SelectedType => Types[Math.Max(0, TypeInput.SelectedIndex)].Type;
    private bool IsDebitCreditNote => SelectedType is TaxDocumentType.DebitNote or TaxDocumentType.CreditNote;

    public ETaxInvoiceDialog(ETaxSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        TypeInput.ItemsSource = Types.Select(t => t.Name).ToList();
        TypeInput.SelectedIndex = Math.Max(0, Array.FindIndex(Types, t => t.Type == settings.Type));
        BuyerKind.ItemsSource = Kinds.Select(k => Loc.T(k.Name)).ToList();
        BuyerKind.SelectedIndex = 0;
        _suggestedNumber = ETaxSettings.NextNumber(settings.LastNumberFor(settings.Type), settings.Type);
        NumberInput.Text = _suggestedNumber;
        ReferenceTypeInput.ItemsSource = Types.Take(4).Select(t => t.Name).ToList();
        ReferenceTypeInput.SelectedIndex = Math.Max(0, Array.FindIndex(Types, t => t.Type == settings.LastInvoiceType && ETaxSettings.IsInvoiceType(t.Type)));
        DateInput.SelectedDate = DateTime.Today;
        VatInput.Text = settings.VatRate.ToString("0.##", CultureInfo.InvariantCulture);
        IncludesVatInput.IsChecked = settings.PricesIncludeVat;
        if (settings.Seller is { } seller)
        {
            SellerName.Text = seller.Name;
            SellerId.Text = seller.Id;
            SellerBranch.Text = seller.Branch;
            SellerFields.Restore(seller);
            SellerPhone.Text = seller.Phone ?? string.Empty;
            SellerEmail.Text = seller.Email ?? string.Empty;
        }

        ItemsGrid.Columns.Add(Column("Description", nameof(ItemRow.Description), new DataGridLength(1, DataGridLengthUnitType.Star)));
        ItemsGrid.Columns.Add(Column("Quantity", nameof(ItemRow.Quantity), new DataGridLength(70), right: true));
        ItemsGrid.Columns.Add(Column("Unit", nameof(ItemRow.Unit), new DataGridLength(70)));
        ItemsGrid.Columns.Add(Column("Unit price", nameof(ItemRow.UnitPrice), new DataGridLength(100), right: true));
        ItemsGrid.Columns.Add(Column("Discount", nameof(ItemRow.Discount), new DataGridLength(85), right: true));
        ItemsGrid.Columns.Add(Column("Amount", nameof(ItemRow.Amount), new DataGridLength(110), right: true, readOnly: true));
        ItemsGrid.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => Update();
        Loaded += (_, _) => (SellerName.Text.Length == 0 ? SellerName : BuyerName).Focus();
        _ready = true;
        OnTypeChanged(this, new RoutedEventArgs());
    }

    public TaxInvoice? Invoice { get; private set; }

    private static DataGridTextColumn Column(string header, string path, DataGridLength width, bool right = false, bool readOnly = false)
    {
        var column = new DataGridTextColumn
        {
            Header = Loc.T(header),
            Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
            Width = width,
            IsReadOnly = readOnly,
        };
        column.ElementStyle = new Style(typeof(TextBlock))
        {
            Setters =
            {
                new Setter(TextBlock.TextAlignmentProperty, right ? TextAlignment.Right : TextAlignment.Left),
                new Setter(FrameworkElement.MarginProperty, new Thickness(6, 0, 6, 0)),
                new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center),
            },
        };

        return column;
    }

    private void OnChanged(object sender, RoutedEventArgs e) => Update();

    private void OnReferenceDateLoaded(object sender, RoutedEventArgs e)
    {
        ReferenceDateInput.ApplyTemplate();
        if (ReferenceDateInput.Template.FindName("PART_TextBox", ReferenceDateInput) is System.Windows.Controls.Primitives.DatePickerTextBox textBox)
        {
            textBox.ApplyTemplate();
            if (textBox.Template.FindName("PART_Watermark", textBox) is ContentControl watermark)
                watermark.Content = Loc.T("Select a date");
        }
    }

    private void OnTypeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (NumberInput.Text == _suggestedNumber)
        {
            _suggestedNumber = ETaxSettings.NextNumber(_settings.LastNumberFor(SelectedType), SelectedType);
            NumberInput.Text = _suggestedNumber;
        }
        ReferencePanel.Visibility = IsDebitCreditNote ? Visibility.Visible : Visibility.Collapsed;
        var prefix = SelectedType == TaxDocumentType.DebitNote ? 'D' : 'C';
        _reasons = IsDebitCreditNote ? Reasons.Where(r => r.Code[0] == prefix).ToArray() : [];
        ReasonInput.ItemsSource = _reasons.Select(r => r.Code + "  " + r.ThaiText).ToList();
        ReasonInput.SelectedIndex = -1;
        OnReasonChanged(sender, e);
    }

    private void OnReasonChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var reason = ReasonInput.SelectedIndex >= 0 ? _reasons[ReasonInput.SelectedIndex] : default;
        ReasonDetailsInput.IsEnabled = reason.Code?.EndsWith("99", StringComparison.Ordinal) == true;
        ReasonDetailsInput.Text = ReasonDetailsInput.IsEnabled ? string.Empty : reason.ThaiText ?? string.Empty;
        Update();
    }

    internal static (bool Ok, TaxDiscount? Discount) ParseDiscount(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return (true, null);
        var percent = text.EndsWith('%');
        if (percent) text = text[..^1].Trim();
        // A zero discount is no discount, so the PDF keeps its plain totals.
        return ParseNumber(text) is >= 0 and var value
            ? (true, value == 0 ? null : new TaxDiscount(value, percent)) : (false, null);
    }

    private static decimal? ParseNumber(string text) => decimal.TryParse(
        text.Replace("฿", string.Empty, StringComparison.Ordinal), NumberStyles.Number,
        CultureInfo.InvariantCulture, out var value) ? value : null;

    private void OnAddressChanged(object? sender, EventArgs e) => Update();

    private void OnBuyerAddressModeChanged(object sender, RoutedEventArgs e)
    {
        if (BuyerFields is null || BuyerFreeAddress is null) return;
        BuyerFields.Visibility = BuyerStructured.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BuyerFreeAddress.Visibility = BuyerStructured.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        Update();
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) => Dispatcher.BeginInvoke(Update);

    private void OnRowEditEnding(object? sender, DataGridRowEditEndingEventArgs e) => Dispatcher.BeginInvoke(Update);

    private void Update()
    {
        if (!_ready)
        {
            return; // Still loading: not every field exists yet.
        }

        BuyerBranch.IsEnabled = BuyerKind.SelectedIndex == 0;
        BuyerId.IsEnabled = BuyerKind.SelectedIndex != 3;
        var invoice = Build();
        var problems = invoice.Problems();
        if (!decimal.TryParse(VatInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var vat) || vat < 0 || vat > 100)
        {
            problems = [.. problems, "Enter the VAT rate in percent."];
        }

        if (_items.Any(i => !i.IsValid && !i.IsEmpty))
        {
            problems = [.. problems, "Quantities and prices are numbers, for example 2 or 1,500.50. Discounts are 50 or 10%."];
        }

        if (!ParseDiscount(BillDiscountInput.Text).Ok)
            problems = [.. problems, "Enter a bill discount such as 50 or 10%."];
        if (IsDebitCreditNote && ParseNumber(OriginalValueInput.Text) is null)
            problems = [.. problems, "Enter the original invoice value before VAT, above zero."];

        var hasDiscount = invoice.Discount is not null || invoice.Lines.Any(l => l.Discount is not null);
        TotalText.Text = invoice.Lines.Count == 0
            ? string.Empty
            : (invoice.IsDebitCreditNote
                ? Loc.F("Original {0} · Correct {1} · Difference {2} · VAT {3} · Total {4}", Money(invoice.OriginalValue), Money(invoice.CorrectValue), Money(invoice.Difference), Money(invoice.Vat), Money(invoice.GrandTotal))
                : hasDiscount
                    ? Loc.F("Before discount {0} · Discount {1} · Before VAT {2} · VAT {3} · Total {4}", Money(invoice.Lines.Sum(l => l.Gross)), Money(invoice.Lines.Sum(l => l.DiscountAmount) + invoice.DiscountAmount), Money(invoice.TaxBasis), Money(invoice.Vat), Money(invoice.GrandTotal))
                    : Loc.F("Before VAT {0} · VAT {1} · Total {2}", Money(invoice.TaxBasis), Money(invoice.Vat), Money(invoice.GrandTotal)))
              + $"  ({AmountInWords.Thai(invoice.GrandTotal)})";
        ErrorText.Text = string.Join("\n", problems.Take(3).Select(Loc.T));
        OkButton.IsEnabled = problems.Count == 0;
    }

    private TaxInvoice Build()
    {
        decimal.TryParse(VatInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var vat);
        var kind = Kinds[Math.Max(0, BuyerKind.SelectedIndex)].Kind;
        return new TaxInvoice(
            Types[Math.Max(0, TypeInput.SelectedIndex)].Type,
            NumberInput.Text.Trim(),
            DateInput.SelectedDate ?? DateTime.Today,
            new TaxParty(SellerName.Text.Trim(), TaxIdKind.TaxId, SellerId.Text.Trim(), SellerBranch.Text.Trim(), SellerFields.Address, SellerFields.Postcode, Blank(SellerEmail.Text), Blank(SellerPhone.Text),
                Blank(SellerFields.BuildingNumber), SellerFields.Province, SellerFields.District, SellerFields.Subdistrict),
            new TaxParty(BuyerName.Text.Trim(), kind, kind == TaxIdKind.None ? string.Empty : BuyerId.Text.Trim(), kind == TaxIdKind.TaxId ? BuyerBranch.Text.Trim() : string.Empty, BuyerStructured.IsChecked == true ? BuyerFields.Address : BuyerAddress.Text.Trim(),
                BuyerStructured.IsChecked == true ? BuyerFields.Postcode : BuyerPostcode.Text.Trim(), Blank(BuyerEmail.Text),
                BuildingNumber: BuyerStructured.IsChecked == true ? Blank(BuyerFields.BuildingNumber) : null,
                Province: BuyerStructured.IsChecked == true ? BuyerFields.Province : null,
                District: BuyerStructured.IsChecked == true ? BuyerFields.District : null,
                Subdistrict: BuyerStructured.IsChecked == true ? BuyerFields.Subdistrict : null,
                UsesThaiAddressFields: BuyerStructured.IsChecked == true),
            _items.Where(i => i.IsValid).Select(i => i.ToLine()).ToList(),
            vat,
            IncludesVatInput.IsChecked == true,
            Blank(NoteInput.Text),
            Discount: ParseDiscount(BillDiscountInput.Text).Discount,
            Reference: IsDebitCreditNote ? new TaxInvoiceReference(ReferenceNumberInput.Text.Trim(),
                ReferenceDateInput.SelectedDate ?? default, Types[Math.Clamp(ReferenceTypeInput.SelectedIndex, 0, 3)].Type,
                ParseNumber(OriginalValueInput.Text) ?? 0) : null,
            Reason: IsDebitCreditNote && ReasonInput.SelectedIndex >= 0
                ? new TaxInvoiceReason(_reasons[ReasonInput.SelectedIndex].Code,
                    ReasonDetailsInput.IsEnabled ? Blank(ReasonDetailsInput.Text) : _reasons[ReasonInput.SelectedIndex].ThaiText) : null);
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Money(decimal value) => value.ToString("#,##0.00", CultureInfo.InvariantCulture);

    private void OnOk(object sender, RoutedEventArgs e)
    {
        ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        Update();
        if (!OkButton.IsEnabled)
        {
            return;
        }

        Invoice = Build();
        DialogResult = true;
    }

    /// <summary>One row of the items table, as typed.</summary>
    public sealed class ItemRow : INotifyPropertyChanged
    {
        private string _quantity = "1";
        private string _unitPrice = string.Empty;
        private string _discount = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Description { get; set; } = string.Empty;

        public string Unit { get; set; } = string.Empty;

        public string Quantity
        {
            get => _quantity;
            set
            {
                _quantity = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Amount)));
            }
        }

        public string UnitPrice
        {
            get => _unitPrice;
            set
            {
                _unitPrice = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Amount)));
            }
        }

        public string Amount => IsValid ? ToLine().Amount.ToString("#,##0.00", CultureInfo.InvariantCulture) : string.Empty;

        public string Discount
        {
            get => _discount;
            set
            {
                _discount = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Amount)));
            }
        }

        public bool IsEmpty => string.IsNullOrWhiteSpace(Description) && string.IsNullOrWhiteSpace(UnitPrice) && string.IsNullOrWhiteSpace(Discount);

        public bool IsValid => !string.IsNullOrWhiteSpace(Description) && Parse(Quantity) is > 0 && Parse(UnitPrice) is >= 0 && ParseDiscount(Discount).Ok;

        public TaxInvoiceLine ToLine() => new(Description.Trim(), Parse(Quantity) ?? 0, Unit.Trim(), Parse(UnitPrice) ?? 0, ParseDiscount(Discount).Discount);

        private static decimal? Parse(string text) =>
            decimal.TryParse(text.Replace("฿", string.Empty, StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
