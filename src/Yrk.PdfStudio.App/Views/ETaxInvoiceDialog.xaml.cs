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

    public ETaxInvoiceDialog(ETaxSettings settings)
    {
        InitializeComponent();
        TypeInput.ItemsSource = Types.Select(t => t.Name).ToList();
        TypeInput.SelectedIndex = Math.Max(0, Array.FindIndex(Types, t => t.Type == settings.Type));
        BuyerKind.ItemsSource = Kinds.Select(k => Loc.T(k.Name)).ToList();
        BuyerKind.SelectedIndex = 0;
        NumberInput.Text = ETaxSettings.NextNumber(settings.LastNumber);
        DateInput.SelectedDate = DateTime.Today;
        VatInput.Text = settings.VatRate.ToString("0.##", CultureInfo.InvariantCulture);
        IncludesVatInput.IsChecked = settings.PricesIncludeVat;
        if (settings.Seller is { } seller)
        {
            SellerName.Text = seller.Name;
            SellerId.Text = seller.Id;
            SellerBranch.Text = seller.Branch;
            SellerAddress.Text = seller.Address;
            SellerPostcode.Text = seller.Postcode;
            SellerPhone.Text = seller.Phone ?? string.Empty;
            SellerEmail.Text = seller.Email ?? string.Empty;
        }

        ItemsGrid.Columns.Add(Column("Description", nameof(ItemRow.Description), new DataGridLength(1, DataGridLengthUnitType.Star)));
        ItemsGrid.Columns.Add(Column("Quantity", nameof(ItemRow.Quantity), new DataGridLength(70), right: true));
        ItemsGrid.Columns.Add(Column("Unit", nameof(ItemRow.Unit), new DataGridLength(70)));
        ItemsGrid.Columns.Add(Column("Unit price", nameof(ItemRow.UnitPrice), new DataGridLength(100), right: true));
        ItemsGrid.Columns.Add(Column("Amount", nameof(ItemRow.Amount), new DataGridLength(110), right: true, readOnly: true));
        ItemsGrid.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => Update();
        Loaded += (_, _) => (SellerName.Text.Length == 0 ? SellerName : BuyerName).Focus();
        _ready = true;
        Update();
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
            problems = [.. problems, "Quantities and prices are numbers, for example 2 or 1,500.50."];
        }

        TotalText.Text = invoice.Lines.Count == 0
            ? string.Empty
            : Loc.F("Before VAT {0} · VAT {1} · Total {2}", Money(invoice.TaxBasis), Money(invoice.Vat), Money(invoice.GrandTotal))
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
            new TaxParty(SellerName.Text.Trim(), TaxIdKind.TaxId, SellerId.Text.Trim(), SellerBranch.Text.Trim(), SellerAddress.Text.Trim(), SellerPostcode.Text.Trim(), Blank(SellerEmail.Text), Blank(SellerPhone.Text)),
            new TaxParty(BuyerName.Text.Trim(), kind, kind == TaxIdKind.None ? string.Empty : BuyerId.Text.Trim(), kind == TaxIdKind.TaxId ? BuyerBranch.Text.Trim() : string.Empty, BuyerAddress.Text.Trim(), BuyerPostcode.Text.Trim(), Blank(BuyerEmail.Text)),
            _items.Where(i => i.IsValid).Select(i => i.ToLine()).ToList(),
            vat,
            IncludesVatInput.IsChecked == true,
            Blank(NoteInput.Text));
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

        public bool IsEmpty => string.IsNullOrWhiteSpace(Description) && string.IsNullOrWhiteSpace(UnitPrice);

        public bool IsValid => !string.IsNullOrWhiteSpace(Description) && Parse(Quantity) is > 0 && Parse(UnitPrice) is >= 0;

        public TaxInvoiceLine ToLine() => new(Description.Trim(), Parse(Quantity) ?? 0, Unit.Trim(), Parse(UnitPrice) ?? 0);

        private static decimal? Parse(string text) =>
            decimal.TryParse(text.Replace("฿", string.Empty, StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
