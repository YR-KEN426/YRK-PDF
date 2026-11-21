using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Yrk.PdfStudio.App.Services;

/// <summary>Applies a whole screen palette, keeping the user's choice during High Contrast.</summary>
public sealed class ThemeManager : IDisposable
{
    private static readonly string[] PalettePaths =
        ["Themes/Colors.Light.xaml", "Themes/Colors.Dark.xaml", "Themes/Colors.HighContrast.xaml"];
    private readonly Application _application;
    private readonly IThemeSystemSource _system;
    private readonly Action<Window, bool> _titleBar;
    private volatile bool _disposed;

    public ThemeManager(Application application) : this(application, new WindowsThemeSystemSource()) { }

    internal ThemeManager(Application application, IThemeSystemSource system, Action<Window, bool>? titleBar = null)
    {
        _application = application;
        _system = system;
        _titleBar = titleBar ?? SetNativeTitleBar;
        _system.UserPreferenceChanged += OnUserPreferenceChanged;
        _system.StaticPropertyChanged += OnStaticPropertyChanged;
        _application.Exit += OnExit;
    }

    public string Preference { get; private set; } = "system";
    internal string EffectiveTheme { get; private set; } = "Light";

    internal static string Normalize(string? theme)
    {
        if (theme is "light" or "dark") return theme;
        return "system";
    }

    public void Apply(string? preference)
    {
        _application.Dispatcher.VerifyAccess();
        if (_disposed) return;
        Preference = Normalize(preference);
        Refresh();
    }

    private void Refresh()
    {
        if (_disposed) return;
        var palette = "Light";
        if (_system.HighContrast) palette = "HighContrast";
        else if (Preference == "dark" || (Preference == "system" && !_system.AppsUseLightTheme)) palette = "Dark";
        var dictionaries = _application.Resources.MergedDictionaries;
        var index = -1;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source is not null && PalettePaths.Any(path => source.EndsWith(path, StringComparison.OrdinalIgnoreCase)))
            {
                index = i;
                break;
            }
        }
        const string missingDictionary = "The application theme dictionary is missing.";
        if (index < 0) throw new InvalidOperationException(missingDictionary);
        if (!dictionaries[index].Source.OriginalString.EndsWith($"Colors.{palette}.xaml", StringComparison.OrdinalIgnoreCase))
            dictionaries[index] = new ResourceDictionary
            {
                Source = new Uri($"/YrkEasyPdf;component/Themes/Colors.{palette}.xaml", UriKind.Relative)
            };
        EffectiveTheme = palette;
        foreach (Window window in _application.Windows) ApplyTitleBar(window);
    }

    public void ApplyTitleBar(Window window)
    {
        if (!_disposed) _titleBar(window, EffectiveTheme == "Dark");
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => QueueRefresh();

    private void OnStaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SystemParameters.HighContrast)) QueueRefresh();
    }

    private void QueueRefresh()
    {
        var dispatcher = _application.Dispatcher;
        if (!_disposed && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            dispatcher.BeginInvoke(new Action(Refresh));
    }

    private void OnExit(object sender, ExitEventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _system.UserPreferenceChanged -= OnUserPreferenceChanged;
        _system.StaticPropertyChanged -= OnStaticPropertyChanged;
        _application.Exit -= OnExit;
    }

    private static void SetNativeTitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = dark ? 1 : 0;
        // Older Windows 10 builds use attribute 19 instead of 20.
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

internal interface IThemeSystemSource
{
    bool AppsUseLightTheme { get; }
    bool HighContrast { get; }
    event UserPreferenceChangedEventHandler UserPreferenceChanged;
    event PropertyChangedEventHandler StaticPropertyChanged;
}

internal sealed class WindowsThemeSystemSource : IThemeSystemSource
{
    public bool AppsUseLightTheme
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return true;
            }
        }
    }

    public bool HighContrast => SystemParameters.HighContrast;
    public event UserPreferenceChangedEventHandler UserPreferenceChanged
    {
        add => SystemEvents.UserPreferenceChanged += value;
        remove => SystemEvents.UserPreferenceChanged -= value;
    }
    public event PropertyChangedEventHandler StaticPropertyChanged
    {
        add => SystemParameters.StaticPropertyChanged += value;
        remove => SystemParameters.StaticPropertyChanged -= value;
    }
}
