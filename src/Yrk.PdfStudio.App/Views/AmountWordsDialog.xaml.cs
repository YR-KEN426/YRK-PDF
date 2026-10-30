using System.Runtime.InteropServices;
using System.Windows;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.App.Views;

/// <summary>Turns an amount into Thai and English words, to copy or to put on the page.</summary>
public partial class AmountWordsDialog : Window
{
    private readonly bool _canPlace;

    /// <param name="canPlace">A document is open, so the words can go on a page.</param>
    public AmountWordsDialog(bool canPlace)
    {
        InitializeComponent();
        _canPlace = canPlace;
        Loaded += (_, _) => AmountInput.Focus();
    }

    /// <summary>The words to put on the page; null when they are not wanted there.</summary>
    public string? WordsToPlace { get; private set; }

    private void OnAmountChanged(object sender, RoutedEventArgs e)
    {
        var known = AmountInWords.TryParse(AmountInput.Text, out var amount);
        ThaiWords.Text = known ? AmountInWords.Thai(amount) : string.Empty;
        EnglishWords.Text = known ? AmountInWords.English(amount) : string.Empty;
        ErrorText.Visibility = known || AmountInput.Text.Trim().Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CopyThai.IsEnabled = CopyEnglish.IsEnabled = known;
        PlaceThai.IsEnabled = PlaceEnglish.IsEnabled = known && _canPlace;
    }

    private void OnCopyThai(object sender, RoutedEventArgs e) => Copy(ThaiWords.Text);

    private void OnCopyEnglish(object sender, RoutedEventArgs e) => Copy(EnglishWords.Text);

    private void OnPlaceThai(object sender, RoutedEventArgs e) => Place(ThaiWords.Text);

    private void OnPlaceEnglish(object sender, RoutedEventArgs e) => Place(EnglishWords.Text);

    private static void Copy(string words)
    {
        try
        {
            Clipboard.SetText(words);
        }
        catch (ExternalException)
        {
            // Another program holds the clipboard for a moment; pressing Copy again works.
        }
    }

    private void Place(string words)
    {
        WordsToPlace = words;
        DialogResult = true;
    }
}
