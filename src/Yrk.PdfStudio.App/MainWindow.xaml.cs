using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App;

public partial class MainWindow : Window
{
    private readonly Func<MainViewModel> _createTab;
    private MainViewModel _viewModel;
    private bool _syncingThumbnailSelection;
    private bool _closeConfirmed;
    private bool _restoringScroll;

    /// <param name="createTab">Makes the view model for a new tab (one per open file).</param>
    public MainWindow(MainViewModel viewModel, Func<MainViewModel> createTab)
    {
        InitializeComponent();
        _createTab = createTab;
        _viewModel = viewModel;
        Tabs.Add(viewModel);
        TabStrip.ItemsSource = Tabs;
        Tabs.CollectionChanged += (_, _) => TabStripBar.Visibility = Tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Attach(viewModel);
        InitializeHome();
        // Alt + the menu's letter opens it, as in Office.
        foreach (var (key, button, open) in new (Key, Button, RoutedEventHandler)[]
        {
            (Key.F, FileMenuButton, OnFileMenuClick), (Key.V, ViewMenuButton, OnViewMenuClick), (Key.T, ToolsMenuButton, OnToolsButtonClick),
            (Key.S, SettingsMenuButton, OnSettingsButtonClick), (Key.H, HelpMenuButton, OnHelpButtonClick),
        })
        {
            InputBindings.Add(new KeyBinding(new CommunityToolkit.Mvvm.Input.RelayCommand(() => open(button, new RoutedEventArgs())), key, ModifierKeys.Alt));
        }

        Loaded += (_, _) =>
        {
            _viewModel.DpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            UpdatePageCursor();
        };
    }

    /// <summary>One view model per open file; the window shows <see cref="_viewModel"/>.</summary>
    public System.Collections.ObjectModel.ObservableCollection<MainViewModel> Tabs { get; } = [];

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || !Tabs.Any(t => t.IsDirty))
        {
            return;
        }

        // Cancel now (Closing cannot wait), then ask for every tab with changes and close once all are settled.
        // The questions run after Closing has returned: WPF refuses Close() and new dialogs while a window is
        // closing, and a "Don't save" answer finishes without ever yielding.
        e.Cancel = true;
        await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        if (await ConfirmAllTabsAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>Asks to save or discard each tab with changes. False when the user cancels.</summary>
    private async Task<bool> ConfirmAllTabsAsync()
    {
        CommitInPlaceEdits();
        foreach (var tab in Tabs.Where(t => t.IsDirty).ToList())
        {
            SwitchTo(tab); // So the user sees which document is meant.
            if (!await tab.ConfirmDiscardOrSaveAsync())
            {
                return false;
            }
        }

        return true;
    }

    // --- Tabs ----------------------------------------------------------------------------------

    private void Attach(MainViewModel tab)
    {
        _viewModel = tab;
        tab.ScrollRequested += OnScrollRequested;
        tab.PropertyChanged += OnViewModelPropertyChanged;
        tab.CommitEditsRequested += OnCommitEditsRequested;
        tab.CommentEditRequested += OnCommentEditRequested;
        tab.SignaturePanelRequested += OnSignaturePanelRequested;
        tab.OpenInNewTabRequested += OnOpenInNewTabRequested;
        tab.CloseTabRequested += OnCloseTabRequested;
        tab.IsActiveTab = true;
        tab.ConfirmAllTabsAsync = ConfirmAllTabsAsync;
        if (IsLoaded)
        {
            tab.DpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        }

        // The page list keeps the old scroll position until the new pages are laid out: hold the
        // viewport reports back, then return to where this tab was.
        _restoringScroll = true;
        var offset = tab.ScrollOffset;
        DataContext = tab;
        Dispatcher.BeginInvoke(
            () =>
            {
                PageScroller.UpdateLayout();
                PageScroller.ScrollToVerticalOffset(offset);
                PageScroller.UpdateLayout();
                _restoringScroll = false;
                ReportViewport();
                UpdatePageCursor();
            },
            DispatcherPriority.Loaded);
    }

    private void Detach(MainViewModel tab)
    {
        tab.ScrollRequested -= OnScrollRequested;
        tab.PropertyChanged -= OnViewModelPropertyChanged;
        tab.CommitEditsRequested -= OnCommitEditsRequested;
        tab.CommentEditRequested -= OnCommentEditRequested;
        tab.SignaturePanelRequested -= OnSignaturePanelRequested;
        tab.OpenInNewTabRequested -= OnOpenInNewTabRequested;
        tab.CloseTabRequested -= OnCloseTabRequested;
        tab.IsActiveTab = false;
    }

    private void SwitchTo(MainViewModel tab)
    {
        if (tab == _viewModel)
        {
            return;
        }

        CommitInPlaceEdits();
        _gesturePage = null;
        _rulerPage = null;
        Detach(_viewModel);
        Attach(tab);
    }

    /// <summary>
    /// Opens a file: in the current tab when it is empty, in the tab that already shows it, or in a new tab.
    /// Used by Open, drag and drop, and files sent from Explorer while the app is running.
    /// </summary>
    public async Task OpenInTabAsync(string path)
    {
        var existing = Tabs.FirstOrDefault(t => t.DocumentPath is { } open && string.Equals(Path.GetFullPath(open), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SwitchTo(existing);
            return;
        }

        if (!_viewModel.HasDocument)
        {
            await _viewModel.OpenFileAsync(path);
            return;
        }

        var previous = _viewModel;
        var tab = _createTab();
        Tabs.Insert(Tabs.IndexOf(previous) + 1, tab);
        SwitchTo(tab);
        await tab.OpenFileAsync(path);
        if (!tab.HasDocument)
        {
            // It could not be opened (and said why): go back to where the user was.
            SwitchTo(previous);
            Tabs.Remove(tab);
            await tab.CloseTabAsync();
        }
    }

    private async Task CloseTabAsync(MainViewModel tab)
    {
        if (tab.IsDirty)
        {
            SwitchTo(tab);
            CommitInPlaceEdits();
            if (!await tab.ConfirmDiscardOrSaveAsync())
            {
                return;
            }
        }

        if (Tabs.Count == 1)
        {
            await tab.CloseTabAsync(); // The last tab stays, empty.
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (tab == _viewModel)
        {
            SwitchTo(Tabs[index == Tabs.Count - 1 ? index - 1 : index + 1]);
        }

        Tabs.Remove(tab);
        await tab.CloseTabAsync();
    }

    /// <summary>Shows the window above others (a file was sent from Explorer).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void SwitchTabBy(int step)
    {
        if (Tabs.Count > 1)
        {
            SwitchTo(Tabs[(Tabs.IndexOf(_viewModel) + step + Tabs.Count) % Tabs.Count]);
        }
    }

    private async void OnOpenInNewTabRequested(object? sender, string path) => await OpenInTabAsync(path);

    private async void OnCloseTabRequested(object? sender, EventArgs e)
    {
        if (sender is MainViewModel tab)
        {
            await CloseTabAsync(tab);
        }
    }

    private void OnCommitEditsRequested(object? sender, EventArgs e) => CommitInPlaceEdits();

    private void OnSignaturePanelRequested(object? sender, EventArgs e)
    {
        _viewModel.IsSidebarOpen = true;
        SignaturesTab.IsSelected = true;
    }

    private void OnTabClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MainViewModel tab })
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                _ = CloseTabAsync(tab);
            }
            else
            {
                SwitchTo(tab);
            }

            e.Handled = true;
        }
    }

    private async void OnTabCloseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MainViewModel tab })
        {
            await CloseTabAsync(tab);
        }
    }

    // A new tab shows the start page, as in a browser.
    private void OnNewTabClick(object sender, RoutedEventArgs e) => ShowHome();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Ctrl+Tab and Ctrl+Shift+Tab move between tabs, Ctrl+PageDown/PageUp too (as in browsers).
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key is Key.Tab or Key.PageDown or Key.PageUp)
        {
            var back = e.Key == Key.PageUp || (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            if (Tabs.Count > 1)
            {
                SwitchTabBy(back ? -1 : 1);
                e.Handled = true;
                return;
            }
        }

        // As in Acrobat: hold Space to drag the page with the hand, H to keep the hand tool on.
        if (_viewModel.HasDocument && Keyboard.Modifiers == ModifierKeys.None && !IsTyping())
        {
            if (e.Key == Key.Space)
            {
                if (!_spaceHeld)
                {
                    _spaceHeld = true;
                    UpdatePageCursor();
                }

                e.Handled = true; // Also keeps Space from pressing the focused toolbar button.
                return;
            }

            if (e.Key == Key.H && !e.IsRepeat)
            {
                HandToggle.IsChecked = HandToggle.IsChecked != true;
                e.Handled = true;
                return;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Space && _spaceHeld)
        {
            _spaceHeld = false;
            UpdatePageCursor();
            e.Handled = true;
        }

        base.OnPreviewKeyUp(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_spaceHeld)
        {
            _spaceHeld = false; // The key-up goes to the other window.
            UpdatePageCursor();
        }
    }

    /// <summary>Keys go to a text box (a form field, the zoom or search box) rather than to the page.</summary>
    private static bool IsTyping() =>
        Keyboard.FocusedElement is TextBoxBase or PasswordBox || Keyboard.FocusedElement is ComboBox { IsEditable: true };

    /// <summary>Moving focus away commits a text box that is being edited on the page.</summary>
    private void CommitInPlaceEdits() => PageScroller.Focus();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _viewModel.DpiScale = newDpi.DpiScaleX;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ShowRulers) or nameof(MainViewModel.Zoom) or nameof(MainViewModel.MeasureScale)
            or nameof(MainViewModel.MeasureUnit) or nameof(MainViewModel.CurrentPageIndex) or nameof(MainViewModel.Pages) or "")
        {
            // After layout, so the page positions are current.
            Dispatcher.BeginInvoke(UpdateRulers, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CurrentPageIndex):
                SyncThumbnailSelection();
                break;
            case nameof(MainViewModel.HasDocument) when _viewModel.HasDocument:
                PageScroller.Focus();
                break;
            case nameof(MainViewModel.IsPlacing) or nameof(MainViewModel.CurrentTool):
                UpdatePageCursor();
                break;
        }
    }

    /// <summary>Hand for moving the page; cross for drawing and placing; I-beam where dragging selects or marks text.</summary>
    private void UpdatePageCursor() =>
        PageScroller.Cursor = _panning ? Cursors.ScrollAll
            : IsHandActive ? Cursors.Hand
            : _viewModel.IsPlacing
            ? Cursors.Cross
            : _viewModel.CurrentTool switch
            {
                AnnotationTool.Select or AnnotationTool.Highlight or AnnotationTool.Underline or AnnotationTool.StrikeOut or AnnotationTool.EditText => Cursors.IBeam,
                _ => Cursors.Cross,
            };

    // --- Page list ---------------------------------------------------------------------------

    private void OnScrollRequested(object? sender, double offset)
    {
        // Wait for layout so the new page sizes are measured before scrolling.
        Dispatcher.InvokeAsync(
            () =>
            {
                PageScroller.UpdateLayout();
                PageScroller.ScrollToVerticalOffset(offset);
                PageScroller.UpdateLayout();
                ReportViewport();
            },
            DispatcherPriority.Loaded);
    }

    private void OnPageScrollerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource == PageScroller)
        {
            ReportViewport();
        }
    }

    private void ReportViewport()
    {
        if (_restoringScroll)
        {
            return; // A tab switch is in progress: the offset still belongs to the previous tab.
        }

        _viewModel.UpdateViewport(PageScroller.VerticalOffset, PageScroller.ViewportWidth, PageScroller.ViewportHeight);
        UpdateRulers();

        // Sharp detail at high zoom, once scrolling or zooming pauses.
        _detailTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(150), System.Windows.Threading.DispatcherPriority.Background, (_, _) => RequestDetail(), Dispatcher);
        _detailTimer.Stop();
        _detailTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _detailTimer;

    /// <summary>Tells the view model which part of each page on screen is visible (page DIPs).</summary>
    private void RequestDetail()
    {
        _detailTimer?.Stop();
        if (PageItems is null)
        {
            return;
        }

        var viewport = new Rect(0, 0, PageScroller.ViewportWidth, PageScroller.ViewportHeight);
        for (var i = 0; i < PageItems.Items.Count; i++)
        {
            if (PageItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container
                || container.DataContext is not PageViewModel page || !container.IsVisible)
            {
                continue;
            }

            var origin = container.TransformToAncestor(PageScroller).Transform(new Point(0, 0));
            var pageRect = new Rect(origin, new Size(page.DisplayWidth, page.DisplayHeight));
            if (!pageRect.IntersectsWith(viewport))
            {
                continue;
            }

            var visible = Rect.Intersect(pageRect, viewport);
            visible.Offset(-origin.X, -origin.Y);
            _viewModel.UpdateDetail(page, visible);
        }
    }

    // --- Hand tool: drag the pages around (also with Space held, or with the middle button) -------

    private bool _spaceHeld;
    private bool _panning;
    private Point _panStart;
    private Point _panStartOffset;

    private bool IsHandActive => HandToggle.IsChecked == true || _spaceHeld;

    private void OnHandToggleChanged(object sender, RoutedEventArgs e)
    {
        UpdatePageCursor();
        PageScroller.Focus(); // So Space and H keep working after clicking the button.
    }

    private void OnPageScrollerPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var hand = e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && IsHandActive);
        if (!hand || _panning || IsOnControl(e.OriginalSource as DependencyObject, middle: e.ChangedButton == MouseButton.Middle))
        {
            return;
        }

        CommitInPlaceEdits();
        _panning = true;
        _panStart = e.GetPosition(PageScroller);
        _panStartOffset = new Point(PageScroller.HorizontalOffset, PageScroller.VerticalOffset);
        PageScroller.CaptureMouse();
        UpdatePageCursor();
        e.Handled = true;
    }

    private void OnPageScrollerPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        var position = e.GetPosition(PageScroller);
        PageScroller.ScrollToHorizontalOffset(_panStartOffset.X - (position.X - _panStart.X));
        PageScroller.ScrollToVerticalOffset(_panStartOffset.Y - (position.Y - _panStart.Y));
        e.Handled = true;
    }

    private void OnPageScrollerPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning)
        {
            PageScroller.ReleaseMouseCapture(); // Ends the drag in OnPageScrollerLostMouseCapture.
            e.Handled = true;
        }
    }

    private void OnPageScrollerLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_panning && e.OriginalSource == PageScroller)
        {
            _panning = false;
            UpdatePageCursor();
        }
    }

    /// <summary>
    /// Scroll bars always work as usual. With the hand tool, form fields and the boxes of placed text and
    /// pictures still take the click (as in Acrobat); the middle button drags the page from anywhere on it.
    /// </summary>
    private bool IsOnControl(DependencyObject? source, bool middle)
    {
        for (var element = source; element is not null && element != PageScroller; element = Parent(element))
        {
            if (element is ScrollBar || (!middle && !_spaceHeld && element is TextBoxBase or ButtonBase or ComboBox or ListBox))
            {
                return true;
            }
        }

        return false;

        static DependencyObject? Parent(DependencyObject element) =>
            element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
    }

    private void OnPageScrollerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        _viewModel.ZoomBy(e.Delta > 0 ? 1 : -1);
    }

    // --- Sidebar -----------------------------------------------------------------------------

    private void SyncThumbnailSelection()
    {
        if (ThumbnailList.SelectedItems.Count > 1)
        {
            return; // Keep a multi-page selection made for page tools.
        }

        _syncingThumbnailSelection = true;
        try
        {
            ThumbnailList.SelectedIndex = _viewModel.HasDocument ? _viewModel.CurrentPageIndex : -1;
            if (ThumbnailList.SelectedItem is { } item)
            {
                ThumbnailList.ScrollIntoView(item);
            }
        }
        finally
        {
            _syncingThumbnailSelection = false;
        }
    }

    private void OnThumbnailSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingThumbnailSelection && ThumbnailList.SelectedItems.Count == 1 && ThumbnailList.SelectedIndex >= 0)
        {
            _viewModel.GoToPage(ThumbnailList.SelectedIndex);
        }
    }

    private void OnThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PageViewModel page)
        {
            _viewModel.RequestThumbnail(page);
        }
    }

    private void OnThumbnailUnloaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PageViewModel page)
        {
            _viewModel.ReleaseThumbnail(page);
        }
    }

    private void OnBookmarkSelected(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _viewModel.NavigateTo(e.NewValue as BookmarkViewModel);

    // --- Toolbar -----------------------------------------------------------------------------

    private void OnOpenExecuted(object sender, ExecutedRoutedEventArgs e) => _viewModel.OpenCommand.Execute(null);

    private void OnFindExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>The toolbar shows the font and size of the form field being typed in.</summary>
    private void OnFormFieldFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FormFieldViewModel field })
        {
            _viewModel.SelectFormField(field);
        }
    }

    private void OnTextBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Deferred so a mouse click does not immediately replace the selection with a caret.
        var textBox = (TextBox)sender;
        Dispatcher.InvokeAsync(textBox.SelectAll, DispatcherPriority.Input);
    }

    private void OnPageInputKeyDown(object sender, KeyEventArgs e)
    {
        var textBox = (TextBox)sender;
        switch (e.Key)
        {
            case Key.Enter:
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                textBox.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape:
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                PageScroller.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnZoomBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            BindingOperations.GetBindingExpression(ZoomBox, ComboBox.TextProperty)?.UpdateSource();
            PageScroller.Focus();
            e.Handled = true;
        }
    }

    private void OnZoomBoxDropDownClosed(object? sender, EventArgs e)
    {
        if (ZoomBox.SelectedItem is string preset)
        {
            _viewModel.ZoomText = preset;
            ZoomBox.SelectedIndex = -1;
            PageScroller.Focus();
        }
    }

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                var command = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                    ? _viewModel.FindPreviousCommand
                    : _viewModel.FindNextCommand;
                command.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                PageScroller.Focus();
                e.Handled = true;
                break;
        }
    }

    // --- Fill & Sign -------------------------------------------------------------------------

    private void OnSaveExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        CommitInPlaceEdits();
        _viewModel.SaveCommand.Execute(null);
    }

    private FrameworkElement? _gesturePage;

    private void OnPageMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var page = (FrameworkElement)sender;
        if (page.DataContext is not PageViewModel pageViewModel || page.ActualWidth <= 0 || page.ActualHeight <= 0)
        {
            return;
        }

        var point = Relative(page, e);
        if (e.ClickCount == 2 && _viewModel.IsMeasuring)
        {
            _viewModel.FinishMeasurementCommand.Execute(null); // The first click already added the last point.
            e.Handled = true;
            return;
        }

        CommitInPlaceEdits();
        if (_viewModel.IsPlacing && !_viewModel.IsPlacingDigitalSignature)
        {
            _viewModel.OnPageClicked(pageViewModel, point.X, point.Y);
            e.Handled = true;
            return;
        }

        _viewModel.OnPageClicked(pageViewModel, point.X, point.Y); // Deselects placed items.
        if (_viewModel.BeginPageGesture(pageViewModel, point))
        {
            _gesturePage = page;
            page.CaptureMouse();
            e.Handled = true;
        }
    }

    // --- Rulers -----------------------------------------------------------------------------

    private PageViewModel? _rulerPage;
    private int _rulerVersion;

    /// <summary>Lines the rulers up with the page under the mouse (or the current page) and sets their scale.</summary>
    private async void UpdateRulers()
    {
        if (!_viewModel.ShowRulers || PageItems is null)
        {
            return;
        }

        var page = _rulerPage is { } hovered && _viewModel.Pages.Contains(hovered)
            ? hovered
            : _viewModel.Pages.ElementAtOrDefault(_viewModel.CurrentPageIndex);
        if (page is null || PageItems.ItemContainerGenerator.ContainerFromItem(page) is not FrameworkElement container || !container.IsVisible)
        {
            return;
        }

        var origin = container.TranslatePoint(new Point(0, 0), PageScroller);
        TopRuler.Origin = origin.X;
        LeftRuler.Origin = origin.Y;
        TopRuler.Length = page.DisplayWidth;
        LeftRuler.Length = page.DisplayHeight;

        var version = ++_rulerVersion;
        var (dipsPerUnit, _) = await _viewModel.GetRulerScaleAsync(page);
        if (version == _rulerVersion)
        {
            TopRuler.DipsPerUnit = LeftRuler.DipsPerUnit = dipsPerUnit;
        }
    }

    private void TrackRulerCursor(PageViewModel page, MouseEventArgs e)
    {
        if (!_viewModel.ShowRulers)
        {
            return;
        }

        var position = e.GetPosition(PageScroller);
        TopRuler.CursorPosition = position.X;
        LeftRuler.CursorPosition = position.Y;
        if (_rulerPage != page)
        {
            _rulerPage = page; // The rulers follow the page the mouse is on.
            UpdateRulers();
        }
    }

    private void OnPageScrollerMouseLeave(object sender, MouseEventArgs e)
    {
        TopRuler.CursorPosition = LeftRuler.CursorPosition = double.NaN;
    }

    private void OnPageMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PageViewModel hovered })
        {
            TrackRulerCursor(hovered, e);
        }

        if (_gesturePage is not null && sender == _gesturePage)
        {
            _viewModel.UpdatePageGesture(Relative(_gesturePage, e));
        }
        else if (_gesturePage is null && _viewModel.IsMeasuring && sender is FrameworkElement { DataContext: PageViewModel page } element)
        {
            _viewModel.HoverPage(page, Relative(element, e));
        }
    }

    private async void OnPageMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_gesturePage is not { } page || sender != page)
        {
            return;
        }

        var point = Relative(page, e);
        _gesturePage = null;
        page.ReleaseMouseCapture();
        e.Handled = true;
        await _viewModel.EndPageGesture(point);
    }

    private void OnPageLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_gesturePage is not null && sender == _gesturePage)
        {
            _gesturePage = null;
            _viewModel.CancelPageGesture(); // For example Alt+Tab in the middle of a drag.
        }
    }

    private void OnPageMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // A measurement label or count mark: offer to remove it.
        if (sender is FrameworkElement { DataContext: PageViewModel page } element && element.ActualWidth > 0
            && _viewModel.MeasureItemAt(page, Relative(element, e)) is { } measureItem)
        {
            var delete = new MenuItem { Header = measureItem is MeasurementViewModel ? "Delete measurement" : "Delete count mark" };
            delete.Click += (_, _) => _viewModel.DeleteMeasureItem(measureItem);
            var itemMenu = new ContextMenu();
            itemMenu.Items.Add(delete);
            itemMenu.IsOpen = true;
            e.Handled = true;
            return;
        }

        if (!_viewModel.HasTextSelection)
        {
            return;
        }

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy, CommandTarget = this });
        menu.Items.Add(new Separator());
        foreach (var (header, kind) in new[] { ("Highlight", PdfAnnotationKind.Highlight), ("Underline", PdfAnnotationKind.Underline), ("Strike out", PdfAnnotationKind.StrikeOut) })
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await _viewModel.MarkupSelectionAsync(kind);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Mouse position as a fraction of the page, kept inside the page.</summary>
    private static NormalizedPoint Relative(FrameworkElement page, MouseEventArgs e)
    {
        var position = e.GetPosition(page);
        return new NormalizedPoint(
            Math.Clamp(position.X / page.ActualWidth, 0, 1),
            Math.Clamp(position.Y / page.ActualHeight, 0, 1));
    }

    private void OnCopyCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _viewModel.HasTextSelection;
        e.Handled = true;
    }

    private void OnCopyExecuted(object sender, ExecutedRoutedEventArgs e) => _viewModel.CopySelection();

    private async void OnDeleteComment(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CommentViewModel comment)
        {
            await _viewModel.DeleteCommentAsync(comment);
        }
    }

    private async void OnCommentEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CommentViewModel comment)
        {
            await _viewModel.SaveCommentAsync(comment);
        }
    }

    /// <summary>A new note: show the comments panel and put the caret in its text.</summary>
    private void OnCommentEditRequested(object? sender, CommentViewModel comment)
    {
        _viewModel.IsSidebarOpen = true;
        CommentsTab.IsSelected = true;
        Dispatcher.InvokeAsync(
            () =>
            {
                CommentList.ScrollIntoView(comment);
                if (CommentList.ItemContainerGenerator.ContainerFromItem(comment) is ListBoxItem container
                    && FindDescendant<TextBox>(container) is { } editor)
                {
                    editor.Focus();
                }
            },
            DispatcherPriority.Loaded);
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnSignButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var signature in _viewModel.SavedSignatures)
        {
            var remove = new Button
            {
                Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12 },
                Style = (Style)FindResource("ToolbarButton"),
                ToolTip = "Delete this saved signature",
                Margin = new Thickness(12, 0, 0, 0),
            };
            remove.Click += (_, args) =>
            {
                args.Handled = true;
                menu.IsOpen = false;
                _viewModel.DeleteSignatureCommand.Execute(signature);
            };

            var header = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(remove, Dock.Right);
            header.Children.Add(remove);
            header.Children.Add(new Image { Source = signature.Image, Height = 36, MaxWidth = 180, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left });
            menu.Items.Add(new MenuItem { Header = header, Command = _viewModel.PlaceSignatureCommand, CommandParameter = signature });
        }

        if (_viewModel.SavedSignatures.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(new MenuItem { Header = "Create new signature…", Command = _viewModel.NewSignatureCommand });
        menu.IsOpen = true;
    }

    private static PlacedItemViewModel? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as PlacedItemViewModel;

    private void OnItemDragStarted(object sender, DragStartedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            CommitInPlaceEdits();
            _viewModel.BeginItemDrag(item);
        }
    }

    private void OnItemMoveDelta(object sender, DragDeltaEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            _viewModel.MoveItem(item, e.HorizontalChange, e.VerticalChange);
        }
    }

    private void OnItemResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            _viewModel.ResizeItem(item, e.HorizontalChange, e.VerticalChange);
        }
    }

    private void OnItemDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            _viewModel.EndItemDrag(item);
        }
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && ItemOf(sender) is { IsText: true } item)
        {
            _viewModel.BeginEditText(item);
            e.Handled = true;
        }
    }

    private void OnTextEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox editor)
        {
            Dispatcher.InvokeAsync(
                () =>
                {
                    editor.Focus();
                    editor.CaretIndex = editor.Text.Length;
                },
                DispatcherPriority.Input);
        }
    }

    private void OnTextEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox editor && ItemOf(sender) is { } item)
        {
            _viewModel.CommitText(item, editor.Text);
        }
    }

    private void OnTextEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CommitInPlaceEdits();
            e.Handled = true;
        }
    }

    // --- Page tools ----------------------------------------------------------------------------

    /// <summary>The pages selected in the sidebar, or the current page when at most one is selected.</summary>
    private List<int> TargetPages()
    {
        if (ThumbnailList.SelectedItems.Count > 1)
        {
            return ThumbnailList.SelectedItems.OfType<PageViewModel>().Select(p => p.Index).Order().ToList();
        }

        return _viewModel.HasDocument ? [_viewModel.CurrentPageIndex] : [];
    }

    private async void OnRotateLeft(object sender, RoutedEventArgs e) => await RunOnTargetPages(pages => _viewModel.RotatePagesAsync(pages, -1));

    private async void OnRotateRight(object sender, RoutedEventArgs e) => await RunOnTargetPages(pages => _viewModel.RotatePagesAsync(pages, 1));

    private async void OnDeletePages(object sender, RoutedEventArgs e) => await RunOnTargetPages(_viewModel.DeletePagesAsync);

    private async void OnExtractPages(object sender, RoutedEventArgs e) => await RunOnTargetPages(_viewModel.ExtractPagesAsync);

    private async void OnInsertPages(object sender, RoutedEventArgs e) =>
        await RunOnTargetPages(pages => _viewModel.InsertPagesFromFileAsync(pages.Max() + 1));

    private async Task RunOnTargetPages(Func<IReadOnlyList<int>, Task> action)
    {
        var pages = TargetPages();
        if (pages.Count > 0)
        {
            CommitInPlaceEdits();
            await action(pages);
        }
    }

    private void OnThumbnailKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            OnDeletePages(sender, e);
            e.Handled = true;
        }
    }

    private void OnEditTabChecked(object sender, RoutedEventArgs e) => _viewModel.CurrentTool = AnnotationTool.EditText;

    private void OnEditTabUnchecked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CurrentTool == AnnotationTool.EditText)
        {
            _viewModel.CurrentTool = AnnotationTool.Select;
        }
    }

    // A line being edited: focus it when it opens; Enter keeps the change, Escape drops it.
    private void OnLineEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
        {
            Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    /// <summary>Enter in the count type name box applies the name (keys would otherwise go to page shortcuts).</summary>
    private void OnCountNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            PageScroller.Focus();
            e.Handled = true;
        }
    }

    private void OnLineEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TextEditViewModel edit } box || e.Key is not (Key.Enter or Key.Escape))
        {
            return;
        }

        e.Handled = true;
        _viewModel.EndTextEdit(edit, box.Text, cancel: e.Key == Key.Escape);
        PageScroller.Focus();
    }

    private void OnLineEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: TextEditViewModel { IsEditing: true } edit } box)
        {
            _viewModel.EndTextEdit(edit, box.Text, cancel: false);
        }
    }

    private void OnMeasureTabChecked(object sender, RoutedEventArgs e) => _viewModel.CurrentTool = AnnotationTool.MeasureDistance;

    private void OnMeasureTabUnchecked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsMeasuring)
        {
            _viewModel.CurrentTool = AnnotationTool.Select;
        }
    }

    private void OnRedactTabChecked(object sender, RoutedEventArgs e) => _viewModel.CurrentTool = AnnotationTool.Redact;

    private void OnRedactTabUnchecked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CurrentTool == AnnotationTool.Redact)
        {
            _viewModel.CurrentTool = AnnotationTool.Select;
        }
    }

    /// <summary>The arrow next to Open: recently opened files, newest first.</summary>
    private void OnRecentMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        AddRecentFiles(menu);
        menu.IsOpen = true;
    }

    /// <summary>The File menu (Alt+F): the commands Office users look for first.</summary>
    private void OnFileMenuClick(object sender, RoutedEventArgs e)
    {
        var hasDocument = _viewModel.HasDocument;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Open…", Command = ApplicationCommands.Open, CommandTarget = this, InputGestureText = "Ctrl+O" });
        var recent = new MenuItem { Header = "Recent files" };
        AddRecentFiles(recent);
        menu.Items.Add(recent);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Save", Command = ApplicationCommands.Save, CommandTarget = this, InputGestureText = "Ctrl+S", IsEnabled = _viewModel.CanSave });
        menu.Items.Add(new MenuItem { Header = "Save a copy…", Command = _viewModel.SaveAsCommand, InputGestureText = "Ctrl+Shift+S", IsEnabled = hasDocument });
        var export = new MenuItem { Header = "Export", IsEnabled = hasDocument };
        foreach (var (header, format) in new[]
        {
            ("Word (.docx)…", Services.ExportFormat.Word), ("Excel (.xlsx)…", Services.ExportFormat.Excel), ("PowerPoint (.pptx)…", Services.ExportFormat.PowerPoint),
            ("Images (PNG)…", Services.ExportFormat.Png), ("Images (JPEG)…", Services.ExportFormat.Jpeg), ("Plain text (.txt)…", Services.ExportFormat.Text),
        })
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await _viewModel.ExportAsync(format);
            export.Items.Add(item);
        }

        menu.Items.Add(export);
        menu.Items.Add(new MenuItem { Header = "Print…", Command = _viewModel.PrintCommand, InputGestureText = "Ctrl+P", IsEnabled = hasDocument });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Protect with password…", Command = _viewModel.ProtectCommand, IsEnabled = hasDocument });
        menu.Items.Add(new MenuItem { Header = "Remove password protection…", Command = _viewModel.RemoveProtectionCommand, IsEnabled = _viewModel.IsProtected });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Close file", Command = _viewModel.CloseCommand, InputGestureText = "Ctrl+W", IsEnabled = hasDocument });
        menu.IsOpen = true;
    }

    /// <summary>The View menu (Alt+V): page size, sidebar, rulers and moving around.</summary>
    private void OnViewMenuClick(object sender, RoutedEventArgs e)
    {
        var hasDocument = _viewModel.HasDocument;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        var home = new MenuItem { Header = "Start page" };
        home.Click += (_, _) => ShowHome();
        menu.Items.Add(home);
        menu.Items.Add(new MenuItem { Header = "Sidebar", Command = _viewModel.ToggleSidebarCommand, IsCheckable = true, IsChecked = _viewModel.IsSidebarOpen });
        menu.Items.Add(new MenuItem { Header = "Rulers", Command = _viewModel.ToggleRulersCommand, InputGestureText = "Ctrl+R", IsCheckable = true, IsChecked = _viewModel.ShowRulers, IsEnabled = hasDocument });
        var hand = new MenuItem { Header = "Hand tool", InputGestureText = "H", IsCheckable = true, IsChecked = HandToggle.IsChecked == true, IsEnabled = hasDocument };
        hand.Click += (_, _) => HandToggle.IsChecked = hand.IsChecked;
        menu.Items.Add(hand);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Fit page", Command = _viewModel.FitPageCommand, InputGestureText = "Ctrl+0", IsEnabled = hasDocument });
        menu.Items.Add(new MenuItem { Header = "Fit width", Command = _viewModel.FitWidthCommand, InputGestureText = "Ctrl+2", IsEnabled = hasDocument });
        menu.Items.Add(new MenuItem { Header = "Actual size", Command = _viewModel.ActualSizeCommand, InputGestureText = "Ctrl+1", IsEnabled = hasDocument });
        menu.Items.Add(new MenuItem { Header = "Zoom in", Command = _viewModel.ZoomInCommand, InputGestureText = "Ctrl++", IsEnabled = hasDocument });
        menu.Items.Add(new MenuItem { Header = "Zoom out", Command = _viewModel.ZoomOutCommand, InputGestureText = "Ctrl+-", IsEnabled = hasDocument });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "First page", Command = _viewModel.FirstPageCommand, InputGestureText = "Ctrl+Home", IsEnabled = hasDocument });
        menu.Items.Add(new MenuItem { Header = "Last page", Command = _viewModel.LastPageCommand, InputGestureText = "Ctrl+End", IsEnabled = hasDocument });
        menu.IsOpen = true;
    }

    /// <summary>Recently opened files, newest first, then "Clear the list".</summary>
    private void AddRecentFiles(ItemsControl menu)
    {
        foreach (var file in Services.RecentFiles.Items)
        {
            menu.Items.Add(new MenuItem
            {
                Header = file.Name.Replace("_", "__", StringComparison.Ordinal), // Not an access key.
                ToolTip = file.Path,
                Tag = UiTranslator.KeepText,
                Command = _viewModel.OpenRecentCommand,
                CommandParameter = file.Path,
            });
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No recent files yet", IsEnabled = false });
        }
        else
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Clear the list", Command = _viewModel.ClearRecentCommand });
        }
    }

    /// <summary>The rulers' corner: pick the unit (shared with measuring) and the drawing scale.</summary>
    private void OnRulerCornerClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Unit", IsEnabled = false });
        foreach (var unit in _viewModel.MeasureUnits)
        {
            var item = new MenuItem { Header = unit, Tag = UiTranslator.KeepText, IsCheckable = true, IsChecked = unit == _viewModel.MeasureUnit };
            item.Click += (_, _) => _viewModel.MeasureUnit = unit;
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var scales = new MenuItem { Header = "Scale" };
        foreach (var scale in _viewModel.MeasureScales)
        {
            var item = new MenuItem { Header = scale.ToString(), Tag = UiTranslator.KeepText, IsCheckable = true, IsChecked = scale == _viewModel.MeasureScale };
            item.Click += (_, _) => _viewModel.MeasureScale = scale;
            scales.Items.Add(item);
        }

        menu.Items.Add(scales);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnToolsButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Combine files…", Command = _viewModel.CombineFilesCommand });
        menu.Items.Add(new MenuItem { Header = "Images to PDF…", Command = _viewModel.ImagesToPdfCommand });
        menu.Items.Add(new MenuItem { Header = "Process many files…", Command = _viewModel.BatchCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Split this PDF…", Command = _viewModel.SplitCommand, IsEnabled = _viewModel.HasDocument });
        menu.Items.Add(new MenuItem { Header = "Compress this PDF…", Command = _viewModel.CompressCommand, IsEnabled = _viewModel.HasDocument });
        menu.Items.Add(new MenuItem { Header = "Save as PDF/A (for archiving)…", Command = _viewModel.SaveAsPdfACommand, IsEnabled = _viewModel.HasDocument });
        menu.Items.Add(new MenuItem { Header = "Watermark, header and page numbers…", Command = _viewModel.PageMarksCommand, IsEnabled = _viewModel.HasDocument });
        menu.Items.Add(new MenuItem { Header = "Export to Word, Excel or images…", Command = _viewModel.ExportCommand, IsEnabled = _viewModel.HasDocument });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Recognize text (OCR)…", Command = _viewModel.RecognizeTextCommand, IsEnabled = _viewModel.HasDocument });
        menu.Items.Add(new MenuItem { Header = "Compare with another version…", Command = _viewModel.CompareCommand, IsEnabled = _viewModel.HasDocument });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Protect with password…", Command = _viewModel.ProtectCommand, IsEnabled = _viewModel.HasDocument });
        menu.Items.Add(new MenuItem { Header = "Remove password protection…", Command = _viewModel.RemoveProtectionCommand, IsEnabled = _viewModel.IsProtected });
        menu.IsOpen = true;
    }

    private void OnSettingsButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };

        // Written in both languages (and never translated) so it can be found whichever is shown.
        var language = new MenuItem { Header = "Language / ภาษา", Tag = UiTranslator.KeepText };
        foreach (var (code, name) in new[] { (Loc.English, "English"), (Loc.Thai, "ไทย (Thai)") })
        {
            language.Items.Add(new MenuItem
            {
                Header = name,
                Tag = UiTranslator.KeepText,
                IsCheckable = true,
                IsChecked = Loc.Language == code,
                Command = _viewModel.SetLanguageCommand,
                CommandParameter = code,
            });
        }

        menu.Items.Add(language);
        menu.Items.Add(new MenuItem { Header = "Updates…", Command = _viewModel.ShowAboutCommand });
        menu.Items.Add(new MenuItem { Header = "Privacy and safety…", Command = _viewModel.ShowPrivacyCommand });
        menu.IsOpen = true;
    }

    private void OnHelpButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        var shortcuts = new MenuItem { Header = "Keyboard shortcuts…" };
        shortcuts.Click += (_, _) => new Views.ShortcutsDialog { Owner = this }.ShowDialog();
        menu.Items.Add(shortcuts);
        menu.Items.Add(LinkItem("Common questions (website)", Services.Feedback.WebsiteUrl + "#faq"));
        menu.Items.Add(new MenuItem { Header = "Suggestions and problems…", Command = _viewModel.ShowFeedbackCommand });
        if (Services.Feedback.SupportUrl.Length > 0)
        {
            menu.Items.Add(LinkItem("Support the developer…", Services.Feedback.SupportUrl));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "About YRK PDF…", Command = _viewModel.ShowAboutCommand });
        menu.IsOpen = true;

        MenuItem LinkItem(string header, string url)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => Services.Feedback.TryOpen(this, url);
            return item;
        }
    }

    // Drag a thumbnail to move pages. The data names the dragged page indices.
    private const string PageDragFormat = "YrkPdfStudio.Pages";
    private Point? _thumbnailDragStart;

    private void OnThumbnailMouseDown(object sender, MouseButtonEventArgs e) =>
        _thumbnailDragStart = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null ? null : e.GetPosition(ThumbnailList);

    private void OnThumbnailMouseMove(object sender, MouseEventArgs e)
    {
        if (_thumbnailDragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(ThumbnailList) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _thumbnailDragStart = null;
        var pages = ThumbnailList.SelectedItems.OfType<PageViewModel>().Select(p => p.Index).Order().ToArray();
        if (pages.Length > 0)
        {
            DragDrop.DoDragDrop(ThumbnailList, new DataObject(PageDragFormat, pages), DragDropEffects.Move);
        }
    }

    private void OnThumbnailDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(PageDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnThumbnailDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(PageDragFormat) is not int[] pages)
        {
            return;
        }

        e.Handled = true;

        // Insert before the page under the pointer, or after it when dropped on its lower half.
        var insertAt = _viewModel.Pages.Count;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { DataContext: PageViewModel target } item)
        {
            insertAt = e.GetPosition(item).Y < item.ActualHeight / 2 ? target.Index : target.Index + 1;
        }

        var destination = insertAt - pages.Count(p => p < insertAt);
        await _viewModel.MovePagesAsync(pages, destination);
    }

    private static T? FindAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null and not T)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return element as T;
    }

    // --- Drag and drop -----------------------------------------------------------------------

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (GetDroppedPdf(e.Data) is not null)
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void OnPreviewDrop(object sender, DragEventArgs e)
    {
        var paths = GetDroppedFiles(e.Data);
        if (paths.Count > 0)
        {
            e.Handled = true;
            Activate();
            foreach (var path in paths)
            {
                await OpenInTabAsync(path); // Several files: one tab each.
            }
        }
    }

    private static string? GetDroppedPdf(IDataObject data) => GetDroppedFiles(data).FirstOrDefault();

    private static List<string> GetDroppedFiles(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(file => MainViewModel.CanOpen(file)).ToList()
            : [];
}
