using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Yrk.PdfStudio.App;

/// <summary>
/// Keeps the top row on one line at medium widths (about 1500 pixels): when it would wrap, Open and Save show only
/// their icons, the menus lose their arrows, and the search and zoom boxes get narrower. The row wraps only when that
/// is still too wide.
/// </summary>
public partial class MainWindow
{
    private const double SearchWidth = 220;
    private const double CompactSearchWidth = 130;
    private const double ZoomWidth = 104;
    private const double CompactZoomWidth = 80;

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
            _labelsWidth = Collapsible().Sum(Measure);
        }

        // The row as it is in full: every button with its words, and the full search and zoom boxes.
        var buttons = TopButtons.Children.Cast<UIElement>().Sum(Measure) + (compact ? _labelsWidth + ZoomWidth - CompactZoomWidth : 0);
        var search = Measure(SearchPanel) - SearchField.Width + SearchWidth;
        SetCompactTopBar(buttons + search > TopBar.ActualWidth);
    }

    /// <summary>What the compact row leaves out: the words of Open and Save, and the arrows of the menus.</summary>
    private IEnumerable<UIElement> Collapsible() =>
        new[] { FileMenuButton, ViewMenuButton, ToolsMenuButton, SettingsMenuButton, HelpMenuButton }
            .Select(b => ((Panel)b.Content).Children[^1])
            .Append(OpenLabel)
            .Append(SaveLabel);

    private void SetCompactTopBar(bool compact)
    {
        var visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var element in Collapsible())
        {
            element.Visibility = visibility;
        }

        SearchField.Width = compact ? CompactSearchWidth : SearchWidth;
        ZoomBox.Width = compact ? CompactZoomWidth : ZoomWidth;
    }

    private static double Measure(UIElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }
}
