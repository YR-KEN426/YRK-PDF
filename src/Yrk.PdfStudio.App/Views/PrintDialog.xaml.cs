using System.Windows;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;

namespace Yrk.PdfStudio.App.Views;

public partial class PrintDialog : Window
{
    private readonly PrintViewModel _viewModel;

    public PrintDialog(PrintViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
    }

    public PrintSettings? Settings { get; private set; }

    private void OnPrint(object sender, RoutedEventArgs e)
    {
        // Text boxes bound on lost focus (custom scale) are committed before reading the settings.
        PrintButton.Focus();
        Settings = _viewModel.CreateSettings();
        if (Settings is not null)
        {
            DialogResult = true;
        }
    }
}
