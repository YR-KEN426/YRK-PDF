using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
        PageClipboard.Shared.SweepStale(DateTime.UtcNow);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskUnobservedException;

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

        _ = Task.Run(() =>
        {
            try { Yrk.PdfStudio.Core.Pdf.Scan.ScanSession.SweepStale(DateTime.UtcNow, TimeSpan.FromDays(1)); }
            catch (Exception ex) { LogMessage("Scan cleanup failed: " + ex); }
        });

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
        PageClipboard.Shared.Dispose();
        _engine?.Dispose();
        PdfTemporaryFiles.Shared.Dispose();
        RecoveryStore.DeleteWritten(); // Closing normally: every change was saved or discarded.
        base.OnExit(e);
    }

    /// <summary>YRK_EASYPDF_LOG_DIR when set; otherwise %LOCALAPPDATA%\YRK\EasyPdf\logs.</summary>
    public static string LogFolder { get; internal set; } = GetDefaultLogFolder(Environment.GetEnvironmentVariable("YRK_EASYPDF_LOG_DIR"));

    internal static string GetDefaultLogFolder(string? logFolder) => !string.IsNullOrWhiteSpace(logFolder)
        ? logFolder
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "logs");

    internal static void LogError(ErrorInfo error, Exception? ex = null) =>
        LogMessage(error.Code + " " + (ex?.ToString() ?? error.Message));

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
            // Open the entry itself, without following a symlink. Inspect and write the same
            // handle, denying concurrent writes/replacement until the append is complete.
            using var handle = CreateFileW(path, 0x40000000, FileShare.Read, IntPtr.Zero,
                4 /* OPEN_ALWAYS */, 0x00200080 /* OPEN_REPARSE_POINT | NORMAL */, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)
                || (info.FileAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
                || info.NumberOfLinks != 1) return;
            using var stream = new FileStream(handle, FileAccess.Write);
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            var length = stream.Length;
            if (length >= MaxLogBytes || bytes.Length > MaxLogBytes - length) return;
            stream.Seek(0, SeekOrigin.End);
            stream.Write(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never make things worse.
        }
    }

    private const long MaxLogBytes = 1_048_576;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, FileShare share,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out LogFileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct LogFileInformation
    {
        public FileAttributes FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

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

    private static int _showingCrashDialog;

    internal static Action<string> CrashMessageBox { get; set; } = message =>
        MessageBox.Show(message, DialogService.Caption, MessageBoxButton.OK, MessageBoxImage.Error);

    internal static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        HandleDispatcherUnhandledException(e);

    internal static void HandleDispatcherUnhandledException(DispatcherUnhandledExceptionEventArgs e, IDialogService? dialogs = null)
    {
        if (Interlocked.CompareExchange(ref _showingCrashDialog, 1, 0) != 0)
        {
            try { LogError(ErrorCatalog.AppDispatcherUnhandled, e.Exception); }
            catch (Exception) { }
            e.Handled = true;
            return;
        }

        try
        {
            // ShowError writes the coded log before displaying the dialog.
            (dialogs ?? new DialogService()).ShowError(ErrorCatalog.AppDispatcherUnhandled, e.Exception);
        }
        catch (Exception)
        {
            // Also cover a dialog service that failed before it could write the log.
            try { LogError(ErrorCatalog.AppDispatcherUnhandled, e.Exception); }
            catch (Exception) { }
            try { CrashMessageBox(Loc.T("Something went wrong. Try again. If the problem continues, restart the app.")); }
            catch (Exception) { }
        }
        finally
        {
            e.Handled = true;
            Volatile.Write(ref _showingCrashDialog, 0);
        }
    }

    internal static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        try { LogError(ErrorCatalog.AppDomainUnhandled, e.ExceptionObject as Exception); }
        catch (Exception) { }

        if (Interlocked.CompareExchange(ref _showingCrashDialog, 1, 0) != 0) return;
        try { CrashMessageBox(Loc.T(ErrorCatalog.AppDomainUnhandled.Message)); }
        catch (Exception) { }
        finally { Volatile.Write(ref _showingCrashDialog, 0); }
    }

    internal static void OnTaskUnobservedException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try { LogError(ErrorCatalog.AppTaskUnobserved, e.Exception); }
        catch (Exception) { }
        finally { e.SetObserved(); }
    }
}
