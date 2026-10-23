using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Views;

public partial class ExportDialog : Window
{
    private readonly int _pageCount;
    private readonly int _currentPage;

    /// <param name="batch">For many files: every page of each, so no page choice; the files are named by the batch.</param>
    public ExportDialog(int pageCount, int currentPage, bool batch = false, ExportFormat? format = null)
    {
        _pageCount = pageCount;
        _currentPage = currentPage;
        InitializeComponent();
        (format switch
        {
            ExportFormat.Excel => ExcelFormat,
            ExportFormat.PowerPoint => PowerPointFormat,
            ExportFormat.Png => PngFormat,
            ExportFormat.Jpeg => JpegFormat,
            ExportFormat.Text => TextFormat,
            _ => WordFormat,
        }).IsChecked = true;
        CurrentPage.Content = Loc.F("Current page ({0})", currentPage + 1);
        if (batch)
        {
            PagesHeading.Visibility = PagesPanel.Visibility = Visibility.Collapsed;
            OkButton.Content = Loc.T("Start");
        }

        Update();
    }

    /// <summary>What to export; the file name is asked for next.</summary>
    public ExportSettings? Settings { get; private set; }

    private ExportFormat Format =>
        ExcelFormat.IsChecked == true ? ExportFormat.Excel
        : PowerPointFormat.IsChecked == true ? ExportFormat.PowerPoint
        : PngFormat.IsChecked == true ? ExportFormat.Png
        : JpegFormat.IsChecked == true ? ExportFormat.Jpeg
        : TextFormat.IsChecked == true ? ExportFormat.Text
        : ExportFormat.Word;

    private void OnChanged(object sender, RoutedEventArgs e) => Update();

    private void OnRangeFocus(object sender, KeyboardFocusChangedEventArgs e) => SomePages.IsChecked = true;

    private void Update()
    {
        if (OkButton is null || ErrorText is null || SheetPerPage is null)
        {
            return; // Raised while the window is being built.
        }

        var format = Format;
        var image = format is ExportFormat.Png or ExportFormat.Jpeg;
        ImagePanel.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        QualityLabel.Visibility = QualityPanel.Visibility = format == ExportFormat.Jpeg ? Visibility.Visible : Visibility.Collapsed;
        SheetPerPage.Visibility = format == ExportFormat.Excel ? Visibility.Visible : Visibility.Collapsed;
        LayoutNote.Visibility = format is ExportFormat.Word or ExportFormat.Excel ? Visibility.Visible : Visibility.Collapsed;

        var (_, problem) = Pages();
        ErrorText.Text = problem ?? string.Empty;
        OkButton.IsEnabled = problem is null;
    }

    private (IReadOnlyList<int> Pages, string? Problem) Pages()
    {
        if (CurrentPage.IsChecked == true)
        {
            return ([_currentPage], null);
        }

        if (SomePages.IsChecked != true)
        {
            return (Enumerable.Range(0, _pageCount).ToList(), null);
        }

        try
        {
            return (PageRanges.Parse(PageRange.Text, _pageCount).SelectMany(p => p).Distinct().ToList(), null);
        }
        catch (FormatException)
        {
            return ([], Loc.F("Enter page numbers from 1 to {0}, for example 1-3, 5, 8-.", _pageCount));
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var (pages, problem) = Pages();
        if (problem is not null || pages.Count == 0)
        {
            return;
        }

        var dpi = DpiBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var value) ? value : 200;
        Settings = new ExportSettings(Format, pages, dpi, (int)QualitySlider.Value, SheetPerPage.IsChecked == true);
        DialogResult = true;
    }
}
