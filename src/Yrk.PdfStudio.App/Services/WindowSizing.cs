using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Yrk.PdfStudio.App.Services;

/// <summary>Keeps a window inside its monitor's usable area, in device-independent units.</summary>
internal static class WindowSizing
{
    private const double Margin = 24;
    private static readonly ConditionalWeakTable<Window, Limits> OriginalLimits = new();

    // Process-local simulation in tests and WindowRenderer; never changes desktop settings.
    internal static Rect? WorkAreaOverride { get; set; }

    public static void FitToWorkArea(Window window)
    {
        if (!OriginalLimits.TryGetValue(window, out var limits))
        {
            limits = new(window.MinWidth, window.MinHeight, window.MaxWidth, window.MaxHeight);
            OriginalLimits.Add(window, limits);
            window.SourceInitialized += (_, _) => Apply(window);
            window.Loaded += (_, _) => Apply(window);
            window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => Apply(window)));
            window.LocationChanged += (_, _) => { if (window.IsLoaded) Apply(window); };
        }
        Apply(window);
    }

    private static void Apply(Window window)
    {
        var limits = OriginalLimits.GetValue(window, _ => throw new InvalidOperationException());
        var area = WorkAreaOverride ?? GetWorkArea(window);
        var maxHeight = Math.Min(limits.MaxHeight, Math.Max(1, area.Height - Margin));
        var maxWidth = Math.Min(limits.MaxWidth, Math.Max(1, area.Width - Margin));
        // Lower the minimum first so WPF cannot coerce the new maximum to the old minimum.
        window.MinHeight = Math.Min(limits.MinHeight, maxHeight);
        window.MinWidth = Math.Min(limits.MinWidth, maxWidth);
        window.MaxHeight = maxHeight;
        window.MaxWidth = maxWidth;
    }

    private static Rect GetWorkArea(Window window)
    {
        // Before first layout, CenterOwner has not positioned the dialog HWND yet.
        var target = !window.IsLoaded && window.Owner is { } owner ? owner : window;
        var handle = new WindowInteropHelper(target).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
        var monitor = MonitorFromWindow(handle, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        var dpi = GetDpiForWindow(handle);
        return ToDeviceIndependentArea(info.Work.Left, info.Work.Top,
            info.Work.Right, info.Work.Bottom, dpi == 0 ? 96u : dpi);
    }

    internal static Rect ToDeviceIndependentArea(int left, int top, int right, int bottom, uint dpi)
    {
        var scale = 96.0 / dpi;
        return new Rect(left * scale, top * scale, (right - left) * scale, (bottom - top) * scale);
    }

    private sealed record Limits(double MinWidth, double MinHeight, double MaxWidth, double MaxHeight);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
