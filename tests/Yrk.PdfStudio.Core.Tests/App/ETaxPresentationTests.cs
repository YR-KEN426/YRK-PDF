using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.Views;
using Yrk.PdfStudio.Core.Pdf.ETax;
using Yrk.PdfStudio.Core.Pdf.Text;
using Yrk.PdfStudio.Testing;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class ETaxPresentationTests
{
    [Theory]
    [InlineData("th")]
    [InlineData("en")]
    public void Displayed_totals_preserve_all_document_discount_and_vat_values(string language)
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp(language);
            var dialog = new ETaxInvoiceDialog(new ETaxSettings());
            T Field<T>(string name) => (T)dialog.FindName(name);
            void Text(string name, string value) => Field<TextBox>(name).Text = value;
            var items = (ObservableCollection<ETaxInvoiceDialog.ItemRow>)Field<DataGrid>("ItemsGrid").ItemsSource;
            Assert.Empty(Field<TextBlock>("TotalText").Text);
            Assert.Empty(Field<TextBlock>("GrandTotalText").Text);
            Text("OriginalValueInput", "5000");
            for (var type = 0; type < 6; type++)
            foreach (var discount in new[] { "", "50", "10%" })
            foreach (var vat in new[] { ("7", false), ("7", true), ("0", false) })
            {
                Field<ComboBox>("TypeInput").SelectedIndex = type;
                items.Clear();
                items.Add(new() { Description = "Sample", Quantity = "2", UnitPrice = "1234.56", Discount = discount });
                items.Add(new() { Description = "Sample B", UnitPrice = "200" });
                Field<CheckBox>("IncludesVatInput").IsChecked = vat.Item2;
                Text("VatInput", vat.Item1);
                Text("BillDiscountInput", discount);
                typeof(ETaxInvoiceDialog).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null);
                var invoice = (TaxInvoice)typeof(ETaxInvoiceDialog).GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null)!;
                static string Money(decimal value) => value.ToString("#,##0.00", CultureInfo.InvariantCulture);
                // Compare to the pre-change combined display, including every subtotal and baht text.
                var legacy = invoice.IsDebitCreditNote
                    ? Loc.F("Original {0} · Correct {1} · Difference {2} · VAT {3}", Money(invoice.OriginalValue), Money(invoice.CorrectValue), Money(invoice.Difference), Money(invoice.Vat))
                    : discount.Length > 0
                        ? Loc.F("Before discount {0} · Discount {1} · Before VAT {2} · VAT {3}", Money(invoice.Lines.Sum(l => l.Gross)), Money(invoice.Lines.Sum(l => l.DiscountAmount) + invoice.DiscountAmount), Money(invoice.TaxBasis), Money(invoice.Vat))
                        : Loc.F("Before VAT {0} · VAT {1}", Money(invoice.TaxBasis), Money(invoice.Vat));
                var subtotal = Field<TextBlock>("TotalText").Text;
                Assert.Equal(legacy, subtotal);
                Assert.Equal(Loc.F("Total {0}", Money(invoice.GrandTotal)) + $"  ({AmountInWords.Thai(invoice.GrandTotal)})", Field<TextBlock>("GrandTotalText").Text);
            }
            items.Clear();
            Assert.Empty(Field<TextBlock>("TotalText").Text);
            Assert.Empty(Field<TextBlock>("GrandTotalText").Text);
            Assert.Empty(app.Errors);
            return true;
        });
    }

    [Theory]
    [InlineData(1.0, "th")]
    [InlineData(1.25, "th")]
    [InlineData(1.5, "th")]
    [InlineData(1.5, "en")]
    public void Document_fields_and_fixed_footer_fit_scaled_1366_by_768_work_area(double scale, string language)
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp(language);
            var dialog = new ETaxInvoiceDialog(new ETaxSettings())
            {
                Width = Math.Min(820, 1366 / scale - 40), Height = 768 / scale - 40,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000,
            };
            try
            {
                dialog.Show();
                WpfTestApp.Layout(dialog);
                WpfTestApp.Pump();
                var scroll = (ScrollViewer)dialog.FindName("FormScrollViewer");
                foreach (var name in new[] { "TypeInput", "NumberInput", "DateInput" })
                {
                    var field = (FrameworkElement)dialog.FindName(name);
                    var bounds = field.TransformToAncestor(scroll).TransformBounds(new Rect(field.RenderSize));
                    Assert.True(bounds.Top >= 0 && bounds.Bottom <= scroll.ViewportHeight, $"{name}: {bounds}; viewport {scroll.ViewportHeight}");
                    Assert.True(bounds.Left >= 0 && bounds.Right <= scroll.ViewportWidth);
                    Assert.True(field.ActualHeight >= 26);
                }
                var total = (TextBlock)dialog.FindName("GrandTotalText");
                var before = total.TransformToAncestor(dialog).Transform(new Point());
                scroll.ScrollToBottom();
                WpfTestApp.Layout(dialog);
                Assert.True(scroll.VerticalOffset > 0);
                Assert.Equal(before, total.TransformToAncestor(dialog).Transform(new Point()));
                Assert.Equal(22, total.FontSize);
                Assert.Equal(FontWeights.Bold, total.FontWeight);
                Assert.Empty(app.Errors);
            }
            finally { dialog.Close(); }
            return true;
        });
    }
}
