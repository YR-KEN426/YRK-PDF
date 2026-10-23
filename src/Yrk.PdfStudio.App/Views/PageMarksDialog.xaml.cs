using System.Windows;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.ViewModels;

namespace Yrk.PdfStudio.App.Views;

public partial class PageMarksDialog : Window
{
    private readonly PageMarksViewModel _viewModel;

    public PageMarksDialog(PageMarksViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
    }

    public PageMarkSettings? Settings { get; private set; }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        // Text boxes bound on lost focus are committed first.
        ((FrameworkElement)sender).Focus();
        if (_viewModel.CanApply)
        {
            Settings = _viewModel.Accept();
            DialogResult = true;
        }
    }
}
