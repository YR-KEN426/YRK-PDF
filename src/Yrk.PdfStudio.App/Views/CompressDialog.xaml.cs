using System.Windows;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Views;

public partial class CompressDialog : Window
{
    public CompressDialog()
    {
        InitializeComponent();
    }

    public PdfCompressionOptions? Options { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Options = Smallest.IsChecked == true
            ? new PdfCompressionOptions(MaxImageDpi: 96, JpegQuality: 60, RecompressLosslessImages: true)
            : Quality.IsChecked == true
                ? new PdfCompressionOptions(MaxImageDpi: 220, JpegQuality: 85)
                : new PdfCompressionOptions(MaxImageDpi: 150, JpegQuality: 75, RecompressLosslessImages: true);
        DialogResult = true;
    }
}
