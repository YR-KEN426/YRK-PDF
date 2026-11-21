using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.Views;
using Yrk.PdfStudio.Testing;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class WindowSizingTests
{
    [Theory]
    [InlineData(144, 800, 480)]
    [InlineData(192, 600, 360)]
    public void Monitor_pixels_convert_using_target_dpi(int dpi, double width, double height)
    {
        var area = WindowSizing.ToDeviceIndependentArea(-1200, 40, 0, 760, (uint)dpi);
        Assert.Equal(width, area.Width);
        Assert.Equal(height, area.Height);
        Assert.Equal(-width, area.Left);
        Assert.Equal(40 * 96.0 / dpi, area.Top, 8);
    }

    [Fact]
    public void Limits_recalculate_at_handle_creation_and_dpi_change_and_restore_design_limits()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            using var area = new SimulatedArea(new Rect(0, 0, 1600, 1000));
            var window = new Window { MinWidth = 980, MinHeight = 640, MaxWidth = 1400, MaxHeight = 900 };
            try
            {
                WindowSizing.FitToWorkArea(window);
                WindowSizing.WorkAreaOverride = new Rect(0, 0, 683, 344);
                new WindowInteropHelper(window).EnsureHandle();
                Assert.Equal(659, window.MaxWidth);
                Assert.Equal(320, window.MaxHeight);
                Assert.Equal(659, window.MinWidth);
                Assert.Equal(320, window.MinHeight);
                WindowSizing.WorkAreaOverride = new Rect(0, 0, 1920, 1080);
                // WPF constructs these event args internally; only delivery matters for this handler.
                var change = (DpiChangedEventArgs)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DpiChangedEventArgs));
                change.RoutedEvent = Window.DpiChangedEvent;
                window.RaiseEvent(change);
                WpfTestApp.Pump();
                Assert.Equal(1400, window.MaxWidth);
                Assert.Equal(900, window.MaxHeight);
                Assert.Equal(980, window.MinWidth);
                Assert.Equal(640, window.MinHeight);
            }
            finally { window.Close(); }
            return true;
        });
    }

    [Theory]
    [InlineData(1.5, "en")]
    [InlineData(2.0, "en")]
    [InlineData(1.5, "th")]
    [InlineData(2.0, "th")]
    public void Long_messages_scroll_while_buttons_stay_visible(double scale, string language)
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp(language);
            using var area = new SimulatedArea(new Rect(0, 0, 1366 / scale, 768 / scale - 40));
            var dialog = new MessageDialog(string.Join("\n", Enumerable.Repeat("A long batch error message with details", 100)),
                "Error", [new("OK", MessageBoxResult.OK, 'O')], MessageBoxResult.OK, MessageBoxResult.OK,
                MessageBoxImage.Error, null);
            try
            {
                dialog.Show(); WpfTestApp.Pump(); dialog.UpdateLayout();
                Assert.True(dialog.ActualHeight <= dialog.MaxHeight + 1);
                var scroll = (ScrollViewer)dialog.FindName("MessageScroll");
                var buttons = (WrapPanel)dialog.FindName("Buttons");
                var before = Bounds(buttons, dialog);
                Assert.True(scroll.ScrollableHeight > 0);
                Assert.True(before.Bottom < dialog.ActualHeight);
                Assert.True(before.Top >= Bounds(scroll, dialog).Bottom);
                scroll.ScrollToBottom(); WpfTestApp.Pump(); dialog.UpdateLayout();
                Assert.True(scroll.VerticalOffset > 0);
                Assert.Equal(before, Bounds(buttons, dialog));
                var message = (TextBlock)dialog.FindName("MessageText");
                Assert.True(Bounds(message, scroll).Bottom <= scroll.ActualHeight + 1);
                Assert.Empty(app.Errors);
            }
            finally { dialog.Close(); }
            return true;
        });
    }

    [Theory]
    [InlineData(1.5, "en")]
    [InlineData(2.0, "en")]
    [InlineData(1.5, "th")]
    [InlineData(2.0, "th")]
    public void Scan_adjustments_scroll_and_scanner_controls_wrap(double scale, string language)
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp(language);
            using var area = new SimulatedArea(new Rect(0, 0, 1366 / scale, 768 / scale - 40));
            var dialog = new ScanDialog();
            try
            {
                dialog.Show(); WpfTestApp.Pump(); dialog.UpdateLayout();
                var row = (WrapPanel)dialog.FindName("ScannerRow");
                foreach (var child in row.Children.OfType<FrameworkElement>())
                {
                    var bounds = Bounds(child, row);
                    Assert.True(bounds.Left >= 0 && bounds.Right <= row.ActualWidth + 1, bounds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Assert.True(bounds.Bottom <= row.ActualHeight + 1);
                }
                if (scale == 2) Assert.True(Bounds((FrameworkElement)dialog.FindName("GlassButton"), row).Top > 0);
                var scroll = (ScrollViewer)dialog.FindName("AdjustmentScroll");
                Assert.True(scroll.ViewportHeight > 0);
                Assert.True(scroll.ScrollableHeight > 0);
                scroll.ScrollToBottom(); WpfTestApp.Pump(); dialog.UpdateLayout();
                Assert.True(scroll.VerticalOffset > 0);
                var last = (FrameworkElement)dialog.FindName("Straighten");
                Assert.True(Bounds(last, scroll).Bottom <= scroll.ActualHeight + 1);
                Assert.Empty(app.Errors);
            }
            finally { dialog.Close(); WpfTestApp.Pump(); }
            return true;
        });
    }

    [Fact]
    public void Handle_initialization_uses_owner_monitor_work_area_and_dpi()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            var owner = new Window { Width = 400, Height = 250, Left = 100, Top = 100,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
            var dialog = new Window();
            try
            {
                owner.Show(); WpfTestApp.Pump();
                WindowSizing.FitToWorkArea(dialog);
                dialog.Owner = owner; // Like callers that assign Owner after constructing a dialog.
                new WindowInteropHelper(dialog).EnsureHandle();
                var handle = new WindowInteropHelper(owner).Handle;
                var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
                Assert.True(GetMonitorInfo(MonitorFromWindow(handle, 2), ref info));
                var expected = WindowSizing.ToDeviceIndependentArea(info.Work.Left, info.Work.Top,
                    info.Work.Right, info.Work.Bottom, GetDpiForWindow(handle));
                Assert.Equal(expected.Width - 24, dialog.MaxWidth, 6);
                Assert.Equal(expected.Height - 24, dialog.MaxHeight, 6);
            }
            finally { dialog.Close(); owner.Close(); }
            return true;
        });
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    private static Rect Bounds(FrameworkElement child, Visual ancestor) =>
        child.TransformToAncestor(ancestor).TransformBounds(new Rect(child.RenderSize));

    private sealed class SimulatedArea : IDisposable
    {
        private readonly Rect? _original = WindowSizing.WorkAreaOverride;
        public SimulatedArea(Rect area) => WindowSizing.WorkAreaOverride = area;
        public void Dispose() => WindowSizing.WorkAreaOverride = _original;
    }
}
