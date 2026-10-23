using System.Globalization;
using System.Windows;
using Yrk.PdfStudio.Core.Pdf.Measure;

namespace Yrk.PdfStudio.App.Views;

public partial class LengthDialog : Window
{
    public LengthDialog(string unit)
    {
        InitializeComponent();
        UnitInput.ItemsSource = Measurement.Units.Select(u => u.Unit).ToList();
        UnitInput.SelectedItem = unit;
        Loaded += (_, _) => ValueInput.Focus();
    }

    public double Value { get; private set; }

    public string Unit => (string)UnitInput.SelectedItem;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var text = ValueInput.Text.Trim();
        if (!(double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
              || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) || value <= 0)
        {
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Value = value;
        DialogResult = true;
    }
}
