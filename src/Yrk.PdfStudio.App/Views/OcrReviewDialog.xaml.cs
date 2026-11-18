using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Yrk.PdfStudio.App.ViewModels;

namespace Yrk.PdfStudio.App.Views;

public partial class OcrReviewDialog : Window
{
    private readonly OcrReviewViewModel _model;
    private TextBox? _editor;
    private int _renderedPage = -1;
    private double _renderedZoom;
    private bool _closed;

    public OcrReviewDialog(OcrReviewViewModel model)
    {
        InitializeComponent();
        DataContext = _model = model;
        if (model.Image is not null) { _renderedPage = model.PageIndex; _renderedZoom = model.Zoom; }
        SkipButton.Visibility = model.HasMoreFiles ? Visibility.Visible : Visibility.Collapsed;
        model.ReviewChanged += OnReviewChanged;
        Loaded += (_, _) => { Refresh(); PageScroller.Focus(); };
        Closed += (_, _) => { _closed = true; model.ReviewChanged -= OnReviewChanged; };
    }

    // Closing through X or Alt+F4 never changes this default.
    public OcrReviewChoice Choice { get; private set; } = OcrReviewChoice.CancelFile;
    private void OnReviewChanged(object? sender, EventArgs e) => Refresh();
    private void Refresh()
    {
        if (_closed) return;
        if (_renderedPage != _model.PageIndex || _renderedZoom != _model.Zoom)
        {
            _renderedPage = _model.PageIndex;
            _renderedZoom = _model.Zoom;
            _ = _model.RenderAsync();
        }
        WordCanvas.Children.Clear();
        _editor = null;
        foreach (var word in _model.Words.Where(w => w.PageIndex == _model.PageIndex))
        {
            var box = word.Original.Bounds;
            var button = new Button { Width = Math.Max(8, box.Width * _model.PageWidth),
                Height = Math.Max(8, box.Height * _model.PageHeight), IsTabStop = false,
                ToolTip = _model.WordText(word) };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetResourceReference(Border.BorderBrushProperty, _model.IsConfirmed(word) ? "AccentBrush" : "CurrentSearchHighlightBrush");
            border.SetValue(Border.BorderThicknessProperty, new Thickness(_model.IsConfirmed(word) ? 3 : 2));
            // Zero-opacity hit paint has no visible theme color and needs no cloned shared brush.
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            AutomationProperties.SetName(button, Loc.F("Review word: {0}", _model.WordText(word)));
            button.Click += (_, _) => _model.SelectWord(word);
            Canvas.SetLeft(button, box.X * _model.PageWidth);
            Canvas.SetTop(button, box.Y * _model.PageHeight);
            WordCanvas.Children.Add(button);
        }
        if (_model.IsEditing && _model.SelectedWord is { } selected)
        {
            var box = selected.Original.Bounds;
            var left = Math.Clamp(box.X * _model.PageWidth, 0, Math.Max(0, _model.PageWidth - 120));
            _editor = ThemeResources.Apply(new TextBox { Text = _model.EditText, MinHeight = 28,
                Width = Math.Min(Math.Max(120, box.Width * _model.PageWidth), _model.PageWidth - left),
                FontSize = 16, Padding = new Thickness(3) },
                (TextBox.BackgroundProperty, "SurfaceBrush"), (TextBox.ForegroundProperty, "TextBrush"), (TextBox.BorderBrushProperty, "AccentBrush"));
            AutomationProperties.SetName(_editor, Loc.T("Word correction (empty deletes)"));
            _editor.TextChanged += (_, _) => _model.EditText = _editor.Text;
            Canvas.SetLeft(_editor, left);
            Canvas.SetTop(_editor, Math.Clamp(box.Y * _model.PageHeight, 0, Math.Max(0, _model.PageHeight - 28)));
            WordCanvas.Children.Add(_editor);
            _editor.Focus();
            _editor.SelectAll();
            _editor.BringIntoView();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            if (key == Key.S) { Finish(OcrReviewChoice.Save); e.Handled = true; }
            else if (key == Key.K && _model.HasMoreFiles) { Finish(OcrReviewChoice.SkipRemaining); e.Handled = true; }
            else if (key == Key.Delete) { _model.DeleteWord(); e.Handled = true; }
            else if (key == Key.Tab)
            {
                _model.CancelEdit();
                SaveButton.Focus();
                e.Handled = true;
            }
            return;
        }
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            if (key is Key.Left or Key.Right) { _model.MovePage(key == Key.Left ? -1 : 1); e.Handled = true; }
            return; // In particular, leave Alt+F4 to the window.
        }
        if (key == Key.Escape)
        {
            if (_model.IsEditing) { _model.CancelEdit(); PageScroller.Focus(); }
            else Finish(OcrReviewChoice.CancelFile);
            e.Handled = true;
        }
        else if (key == Key.Enter && _editor?.IsKeyboardFocusWithin == true)
        {
            _model.ConfirmEdit();
            if (!_model.IsEditing) PageScroller.Focus();
            e.Handled = true;
        }
        else if (key == Key.Tab && PageScroller.IsKeyboardFocusWithin)
        {
            _model.NextWord(modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (_editor?.IsKeyboardFocusWithin != true && PageScroller.IsKeyboardFocusWithin && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            _model.NextWord(key is Key.Left or Key.Up ? -1 : 1);
            e.Handled = true;
        }
    }

    private void Finish(OcrReviewChoice choice) { Choice = choice; DialogResult = choice != OcrReviewChoice.CancelFile; }
    private void OnSave(object sender, RoutedEventArgs e) => Finish(OcrReviewChoice.Save);
    private void OnCancel(object sender, RoutedEventArgs e) => Finish(OcrReviewChoice.CancelFile);
    private void OnSkip(object sender, RoutedEventArgs e) => Finish(OcrReviewChoice.SkipRemaining);
    private void OnNextWord(object sender, RoutedEventArgs e) => _model.NextWord();
    private void OnDelete(object sender, RoutedEventArgs e) => _model.DeleteWord();
    private void OnPreviousPage(object sender, RoutedEventArgs e) => _model.MovePage(-1);
    private void OnNextPage(object sender, RoutedEventArgs e) => _model.MovePage(1);
    private void OnZoomOut(object sender, RoutedEventArgs e) => _model.ChangeZoom(1 / 1.25);
    private void OnZoomIn(object sender, RoutedEventArgs e) => _model.ChangeZoom(1.25);
}
