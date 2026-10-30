using System.Windows;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App.Views;

/// <summary>Shows what the scan check found (tilted pages, dark edges, blank pages) and lets the user pick what to fix.</summary>
public partial class ScanCleanupDialog : Window
{
    public ScanCleanupDialog(IReadOnlyList<int> tilted, IReadOnlyList<int> edged, IReadOnlyList<int> blank)
    {
        InitializeComponent();
        Setup(StraightenOption, StraightenText, tilted, "Straighten 1 tilted page ({0})", "Straighten {0} tilted pages ({1})");
        Setup(EdgesOption, EdgesText, edged, "Trim the dark scanner edges on 1 page ({0})", "Trim the dark scanner edges on {0} pages ({1})");
        Setup(BlankOption, BlankText, blank, "Delete 1 blank page ({0})", "Delete {0} blank pages ({1})");
    }

    public ScanCleanupChoice? Choice { get; private set; }

    private static void Setup(UIElement option, System.Windows.Controls.TextBlock text, IReadOnlyList<int> pages, string one, string many)
    {
        if (pages.Count == 0)
        {
            option.Visibility = Visibility.Collapsed;
            ((System.Windows.Controls.CheckBox)option).IsChecked = false;
            return;
        }

        var list = ViewModels.MainViewModel.FormatPageList(pages);
        text.Text = pages.Count == 1 ? Loc.F(one, list) : Loc.F(many, pages.Count, list);
    }

    private void OnChanged(object sender, RoutedEventArgs e) =>
        OkButton.IsEnabled = StraightenOption.IsChecked == true || EdgesOption.IsChecked == true || BlankOption.IsChecked == true;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Choice = new ScanCleanupChoice(StraightenOption.IsChecked == true, EdgesOption.IsChecked == true, BlankOption.IsChecked == true);
        DialogResult = true;
    }
}
