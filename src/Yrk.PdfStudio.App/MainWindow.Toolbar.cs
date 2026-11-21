using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Yrk.PdfStudio.App;

/// <summary>Fits the document toolbar and expands search into the available space.</summary>
public partial class MainWindow
{
    private const double SearchWidth = 220;
    private bool _fitQueued;
    private bool _searchExpanded;

    private void InitializeTopBar()
    {
        TopBar.SizeChanged += (_, _) => QueueFitTopBar();
        SearchBox.GotKeyboardFocus += (_, _) => { _searchExpanded = true; QueueFitTopBar(); };
        SearchPanel.IsKeyboardFocusWithinChanged += (_, _) =>
        {
            if (!SearchPanel.IsKeyboardFocusWithin && string.IsNullOrEmpty(SearchBox.Text))
                _searchExpanded = false;
            QueueFitTopBar();
        };
        SearchBox.TextChanged += (_, _) =>
        {
            if (!SearchPanel.IsKeyboardFocusWithin && string.IsNullOrEmpty(SearchBox.Text))
                _searchExpanded = false;
            QueueFitTopBar();
        };
        SearchPanel.IsVisibleChanged += (_, _) => QueueFitTopBar();
        void LanguageChanged(object? sender, EventArgs e) => QueueFitTopBar();
        Localization.Loc.LanguageChanged += LanguageChanged;
        Closed += (_, _) => Localization.Loc.LanguageChanged -= LanguageChanged;
        InputBindings.Add(new KeyBinding(new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
        {
            if (_viewModel.ShowsDocument) { PageInput.Focus(); PageInput.SelectAll(); }
        }), Key.G, ModifierKeys.Control));
    }

    private void OnSearchIconClick(object sender, RoutedEventArgs e) => FocusDocumentSearch();

    private void FocusDocumentSearch()
    {
        _searchExpanded = true;
        FitTopBar(); // Reveal the editor before moving keyboard focus into it.
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void QueueFitTopBar()
    {
        if (_fitQueued) return;
        _fitQueued = true;
        Dispatcher.BeginInvoke(FitTopBar, DispatcherPriority.Loaded);
    }

    private void FitTopBar()
    {
        _fitQueued = false;
        if (!_viewModel.ShowsDocument || TopBar.ActualWidth <= 0) return;
        var iconOnly = ActualWidth < 1000 && !_searchExpanded && string.IsNullOrEmpty(SearchBox.Text);
        SearchIconButton.Visibility = iconOnly ? Visibility.Visible : Visibility.Collapsed;
        SearchControls.Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
        SearchField.Width = SearchWidth;

        // Measure the full labels each time, including after a language change while compact.
        SetCompactTopBar(false);
        // Sum the parts instead of measuring the panel: a panel keeps its old cached size after the field width changes.
        var searchExtras = SearchPanel.Margin.Left + SearchPanel.Margin.Right + (iconOnly
            ? Measure(SearchIconButton)
            : SearchControls.Children.Cast<UIElement>().Where(c => c != SearchField).Sum(Measure));
        var buttons = TopButtons.Children.Cast<UIElement>().Sum(Measure);
        var targetWidth = _searchExpanded || !string.IsNullOrEmpty(SearchBox.Text) ? 400 : SearchWidth;
        if (buttons + searchExtras + (iconOnly ? 0 : targetWidth) > TopBar.ActualWidth)
        {
            SetCompactTopBar(true);
            buttons = TopButtons.Children.Cast<UIElement>().Sum(Measure);
        }
        if (!iconOnly)
        {
            var available = TopBar.ActualWidth - buttons - searchExtras;
            SearchField.Width = Math.Clamp(available, 160, targetWidth);
        }
    }

    private IEnumerable<UIElement> Collapsible() =>
        new[] { FileMenuButton, ViewMenuButton, InsertMenuButton, ToolsMenuButton, SettingsMenuButton, HelpMenuButton }
            .Select(b => ((Panel)b.Content).Children[^1])
            .Append(OpenLabel)
            .Append(SaveLabel);

    private void SetCompactTopBar(bool compact)
    {
        foreach (var element in Collapsible())
            element.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private static double Measure(UIElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }
}
