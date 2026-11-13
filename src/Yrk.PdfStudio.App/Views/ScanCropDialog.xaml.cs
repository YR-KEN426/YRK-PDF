using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Views;

public partial class ScanCropDialog : Window
{
    private Rect _rect, _initial;
    private Point _start;
    private bool _left, _right, _top, _bottom, _drag;
    public NormalizedRect? Crop { get; private set; }

    public ScanCropDialog(BitmapSource image, NormalizedRect? crop)
    {
        InitializeComponent();
        PageCanvas.Width = PageImage.Width = image.PixelWidth;
        PageCanvas.Height = PageImage.Height = image.PixelHeight;
        PageImage.Source = image;
        var r = crop ?? new NormalizedRect(0, 0, 1, 1);
        _rect = new(r.X * image.PixelWidth, r.Y * image.PixelHeight, r.Width * image.PixelWidth, r.Height * image.PixelHeight);
        Draw();
    }
    private void Draw()
    {
        Canvas.SetLeft(Frame, _rect.Left); Canvas.SetTop(Frame, _rect.Top);
        Frame.Width = _rect.Width; Frame.Height = _rect.Height;
        Place(TopLeft, _rect.Left, _rect.Top); Place(TopRight, _rect.Right, _rect.Top);
        Place(BottomLeft, _rect.Left, _rect.Bottom); Place(BottomRight, _rect.Right, _rect.Bottom);
    }
    private static void Place(FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x - element.Width / 2); Canvas.SetTop(element, y - element.Height / 2);
    }
    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(PageCanvas);
        var tolerance = 14 * PageCanvas.Width / Math.Max(1, ActualWidth - 36);
        if (!_rect.Contains(_start) && (_start.X < _rect.Left - tolerance || _start.X > _rect.Right + tolerance || _start.Y < _rect.Top - tolerance || _start.Y > _rect.Bottom + tolerance)) return;
        _left = Math.Abs(_start.X - _rect.Left) < tolerance;
        _right = !_left && Math.Abs(_start.X - _rect.Right) < tolerance;
        _top = Math.Abs(_start.Y - _rect.Top) < tolerance;
        _bottom = !_top && Math.Abs(_start.Y - _rect.Bottom) < tolerance;
        _initial = _rect;
        _drag = true;
        PageCanvas.CaptureMouse();
    }
    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_drag) return;
        var delta = e.GetPosition(PageCanvas) - _start;
        double left = _initial.Left, right = _initial.Right, top = _initial.Top, bottom = _initial.Bottom;
        var minimum = Math.Min(10, Math.Min(_initial.Width, _initial.Height));
        if (!_left && !_right && !_top && !_bottom)
        {
            left = Math.Clamp(left + delta.X, 0, PageCanvas.Width - _initial.Width);
            top = Math.Clamp(top + delta.Y, 0, PageCanvas.Height - _initial.Height);
            right = left + _initial.Width; bottom = top + _initial.Height;
        }
        else
        {
            if (_left) left = Math.Clamp(left + delta.X, 0, right - minimum);
            if (_right) right = Math.Clamp(right + delta.X, left + minimum, PageCanvas.Width);
            if (_top) top = Math.Clamp(top + delta.Y, 0, bottom - minimum);
            if (_bottom) bottom = Math.Clamp(bottom + delta.Y, top + minimum, PageCanvas.Height);
        }
        _rect = new(new Point(left, top), new Point(right, bottom));
        Draw();
    }
    private void OnUp(object sender, MouseButtonEventArgs e) { _drag = false; PageCanvas.ReleaseMouseCapture(); }
    private void OnApply(object sender, RoutedEventArgs e)
    {
        var x = Math.Clamp(_rect.X / PageCanvas.Width, 0, 1);
        var y = Math.Clamp(_rect.Y / PageCanvas.Height, 0, 1);
        Crop = new(x, y, Math.Min(_rect.Width / PageCanvas.Width, 1 - x), Math.Min(_rect.Height / PageCanvas.Height, 1 - y));
        DialogResult = true;
    }
    private void OnClear(object sender, RoutedEventArgs e) { Crop = null; DialogResult = true; }
}
