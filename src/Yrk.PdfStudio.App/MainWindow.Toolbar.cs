using System.Windows;
using System.Windows.Threading;

namespace Yrk.PdfStudio.App;

/// <summary>
/// Keeps the top row on one line at medium widths (about 1500 pixels): when it would wrap, Open and Save show only
/// their icons and the search box gets narrower. The row wraps only when that is still too wide.
/// </summary>
public partial class MainWindow
{
    private const double SearchWidth = 220;
    private const double CompactSearchWidth = 150;

    private double _labelsWidth;
    private bool _fitQueued;

    private void InitializeTopBar()
    {
        TopBar.SizeChanged += (_, _) => QueueFitTopBar();
        SearchPanel.SizeChanged += (_, _) => QueueFitTopBar();
        Localization.Loc.LanguageChanged += (_, _) => QueueFitTopBar();
    }

    private void QueueFitTopBar()
    {
        if (!_fitQueued)
        {
            _fitQueued = true;
            Dispatcher.BeginInvoke(FitTopBar, DispatcherPriority.Loaded);
        }
    }

    private void FitTopBar()
    {
        _fitQueued = false;
        var compact = OpenLabel.Visibility != Visibility.Visible;
        if (!compact)
        {
            _labelsWidth = Measure(OpenLabel) + Measure(SaveLabel);
        }

        // The row as it is in full: every button with its words, and the full search box.
        var buttons = TopButtons.Children.Cast<UIElement>().Sum(Measure) + (compact ? _labelsWidth : 0);
        var search = Measure(SearchPanel) - SearchField.Width + SearchWidth;
        SetCompactTopBar(buttons + search > TopBar.ActualWidth);
    }

    private void SetCompactTopBar(bool compact)
    {
        var visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        OpenLabel.Visibility = visibility;
        SaveLabel.Visibility = visibility;
        SearchField.Width = compact ? CompactSearchWidth : SearchWidth;
    }

    private static double Measure(UIElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }
}
