using System.Windows;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App.Views;

public partial class CertifiedCopyDialog : Window
{
    private bool _ready;

    public CertifiedCopyDialog(IReadOnlyList<SavedSignature> signatures)
    {
        InitializeComponent();

        // null = "no signature"; then the user's saved signatures, newest first.
        var choices = new List<SavedSignature?> { null };
        choices.AddRange(signatures);
        SignatureChoice.ItemsSource = choices;
        SignatureChoice.SelectedIndex = signatures.Count > 0 ? 1 : 0;

        _ready = true;
        UpdatePreview();
        Loaded += (_, _) => PurposeInput.Focus();
    }

    public CertifiedCopyOptions? Options { get; private set; }

    private CertifiedCopyOptions CurrentOptions => new(
        PurposeInput.Text,
        DateBox.IsChecked == true,
        (SignatureChoice.SelectedItem as SavedSignature)?.Image);

    private void OnOptionsChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (_ready)
        {
            Preview.Source = StampRenderer.RenderCertifiedCopy(CurrentOptions, pixelsPerPoint: 2).Image;
        }
    }

    private void OnPlace(object sender, RoutedEventArgs e)
    {
        Options = CurrentOptions;
        DialogResult = true;
    }
}
