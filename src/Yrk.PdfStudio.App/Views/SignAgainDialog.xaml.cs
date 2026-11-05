using System.Windows;

namespace Yrk.PdfStudio.App.Views;

public partial class SignAgainDialog : Window
{
    public SignAgainDialog(int signatureCount, bool hasUnsavedEdits)
    {
        InitializeComponent();
        WarningText.Text = signatureCount == 1
            ? Loc.T("This file already has 1 signature. Signing again adds another signature after it; the earlier signature stays.")
            : Loc.F("This file already has {0} signatures. Signing again adds another signature after them; the earlier signatures stay.", signatureCount);
        UnsavedText.Visibility = hasUnsavedEdits ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnContinue(object sender, RoutedEventArgs e) => DialogResult = true;
}
