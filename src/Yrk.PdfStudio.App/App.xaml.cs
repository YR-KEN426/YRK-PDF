using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.App;

public partial class App : Application
{
    private PdfiumEngine? _engine;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try { CleanupOldLogs(LogFolder, DateTime.Now); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }

        var language = AppSettings.Load().Language;
        Loc.Language = language is Loc.English or Loc.Thai ? language : Loc.SystemDefault;
        UiTranslator.Start();

        // Claim preview leases before cleanup, and clean before opening command-line documents.
        foreach (var path in e.Args.Where(File.Exists))
        {
            try { PdfTemporaryFiles.Shared.RetainForPath(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try
        {
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            var siblings = System.Diagnostics.Process.GetProcessesByName(current.ProcessName);
            try { PdfTemporaryFiles.Shared.Cleanup(includeLegacy: SingleInstance.IsFirst && siblings.Length == 1); }
            finally { foreach (var process in siblings) process.Dispose(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        _engine = new PdfiumEngine();
        var viewModel = new MainViewModel(_engine, new DialogService(), new SignatureStore());
        var window = new MainWindow(viewModel, () => new MainViewModel(_engine, new DialogService(), new SignatureStore()));
        MainWindow = window;
        window.Show();
        _ = OpenArgsAsync(window, e.Args);

        // Files opened from Explorer while the app is running arrive here (see Program).
        if (SingleInstance.IsFirst)
        {
            _ = window.OfferRecoveryAsync(); // Copies left by a run that was cut off before saving.
            SingleInstance.Listen(args => Dispatcher.InvokeAsync(() =>
            {
                window.BringToFront();
                _ = OpenArgsAsync(window, args);
            }));
        }

        _ = viewModel.CheckForUpdatesInBackgroundAsync();
    }

    /// <summary>
    /// Opening a file from Explorer ("Open with", file association) passes its path as the first argument;
    /// several selected files come as several arguments. "file --compare other" opens the file and
    /// compares it with another version.
    /// </summary>
    private static async Task OpenArgsAsync(MainWindow window, string[] args)
    {
        if (args.Length > 2 && args[1] == "--compare")
        {
            if (File.Exists(args[0]) && File.Exists(args[2]))
            {
                await window.OpenInTabAsync(args[0]);
                await ((MainViewModel)window.DataContext).CompareWithAsync(args[2]);
            }

            return;
        }

        foreach (var path in args.Where(File.Exists))
        {
            try { PdfTemporaryFiles.Shared.RetainForPath(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            await window.OpenInTabAsync(path);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _engine?.Dispose();
        PdfTemporaryFiles.Shared.Dispose();
        RecoveryStore.DeleteWritten(); // Closing normally: every change was saved or discarded.
        base.OnExit(e);
    }

    /// <summary>%LOCALAPPDATA%\YRK\EasyPdf\logs, for diagnosing problems users report.</summary>
    public static string LogFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "logs");

    private static void LogError(Exception exception) => LogMessage(exception.ToString());

    public static void LogMessage(string message)
        => LogMessage(message, LogFolder, DateTime.Now);

    internal static void LogMessage(string message, string logFolder, DateTime now)
    {
        try
        {
            Directory.CreateDirectory(logFolder);
            if ((File.GetAttributes(logFolder) & FileAttributes.ReparsePoint) != 0) return;
            // Invariant culture: with Thai regional settings the year would otherwise be Buddhist (2569).
            var path = Path.Combine(logFolder, $"errors-{now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");
            var content = $"[{now.ToString("O", CultureInfo.InvariantCulture)}] {message}{Environment.NewLine}{Environment.NewLine}";
            var length = new FileInfo(path).Exists ? new FileInfo(path).Length : 0;
            if (length >= MaxLogBytes || System.Text.Encoding.UTF8.GetByteCount(content) > MaxLogBytes - length) return;
            File.AppendAllText(path, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never make things worse.
        }
    }

    private const long MaxLogBytes = 1_048_576;

    internal static void CleanupOldLogs(string logFolder, DateTime now)
    {
        if (!Directory.Exists(logFolder) || (File.GetAttributes(logFolder) & FileAttributes.ReparsePoint) != 0) return;

        foreach (var path in Directory.EnumerateFiles(logFolder, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            const string prefix = "errors-";
            const string suffix = ".log";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var dateText = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
            if (!DateTime.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date.Date >= now.Date.AddDays(-14)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            File.Delete(path);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        MessageBox.Show(
            MainWindow,
            Loc.F("Something went wrong:\n\n{0}", e.Exception.Message),
            DialogService.Caption,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
