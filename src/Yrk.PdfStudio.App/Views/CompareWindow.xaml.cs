using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Yrk.PdfStudio.App.ViewModels;

namespace Yrk.PdfStudio.App.Views;

public partial class CompareWindow : Window
{
    public static readonly DependencyProperty RegionBorderProperty =
        DependencyProperty.Register(nameof(RegionBorder), typeof(Thickness), typeof(CompareWindow), new PropertyMetadata(new Thickness(2)));

    private const double MinZoom = 0.05;
    private const double MaxZoom = 8;
    private readonly CompareViewModel _viewModel;
    private bool _fitted;

    public CompareWindow(CompareViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        viewModel.Compared += (_, _) =>
        {
            if (!_fitted)
            {
                _fitted = true;
                Dispatcher.BeginInvoke(Fit, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };
        Loaded += async (_, _) => await viewModel.CompareAsync();
        Closed += async (_, _) => await viewModel.DisposeAsync();
    }

    /// <summary>Region outlines stay about two screen pixels wide at any zoom.</summary>
    public Thickness RegionBorder
    {
        get => (Thickness)GetValue(RegionBorderProperty);
        set => SetValue(RegionBorderProperty, value);
    }

    private double Zoom => SheetScale.ScaleX;

    private void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        SheetScale.ScaleX = SheetScale.ScaleY = zoom;
        RegionBorder = new Thickness(2 / zoom);
    }

    private void Fit()
    {
        if (_viewModel.Image is not { } image || Scroller.ViewportWidth <= 0)
        {
            return;
        }

        SetZoom(Math.Min((Scroller.ViewportWidth - 24) / image.PixelWidth, (Scroller.ViewportHeight - 24) / image.PixelHeight));
    }

    private void OnRegionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (RegionList.SelectedItem is not CompareRegionViewModel item || _viewModel.Image is null)
        {
            return;
        }

        // Show the change with some room around it, no closer than 100 %.
        var region = item.Region;
        var zoom = Math.Min(1.0, Math.Min(Scroller.ViewportWidth / (region.Width * 3 + 60), Scroller.ViewportHeight / (region.Height * 3 + 60)));
        SetZoom(Math.Max(zoom, Math.Min(Scroller.ViewportWidth / _viewModel.Image.PixelWidth, 1)));
        Sheet.UpdateLayout();
        Scroller.UpdateLayout();
        var centre = Sheet.TranslatePoint(new Point(region.X + (region.Width / 2.0), region.Y + (region.Height / 2.0)), Scroller);
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + centre.X - (Scroller.ViewportWidth / 2));
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + centre.Y - (Scroller.ViewportHeight / 2));
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        // Zoom around the mouse: keep the image point under the cursor where it is.
        e.Handled = true;
        var mouse = e.GetPosition(Scroller);
        var onSheet = e.GetPosition(Sheet);
        SetZoom(Zoom * (e.Delta > 0 ? 1.25 : 0.8));
        Scroller.UpdateLayout();
        var after = Sheet.TranslatePoint(onSheet, Scroller);
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + after.X - mouse.X);
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + after.Y - mouse.Y);
    }
}
