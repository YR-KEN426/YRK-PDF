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

        var language = AppSettings.Load().Language;
        Loc.Language = language is Loc.English or Loc.Thai ? language : Loc.SystemDefault;
        UiTranslator.Start();

        _engine = new PdfiumEngine();
        var viewModel = new MainViewModel(_engine, new DialogService(), new SignatureStore());
        var window = new MainWindow(viewModel, () => new MainViewModel(_engine, new DialogService(), new SignatureStore()));
        MainWindow = window;
        window.Show();
        _ = OpenArgsAsync(window, e.Args);

        // Files opened from Explorer while the app is running arrive here (see Program).
        if (SingleInstance.IsFirst)
        {
            DeletePageSnapshots(); // Left over if the app was stopped before it could clean up.
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
            await window.OpenInTabAsync(path);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _engine?.Dispose();
        DeletePageSnapshots();
        RecoveryStore.DeleteWritten(); // Closing normally: every change was saved or discarded.
        base.OnExit(e);
    }

    /// <summary>Removes the temporary copies of deleted pages (kept for undo) so no document content stays behind.</summary>
    private static void DeletePageSnapshots()
    {
        try
        {
            if (Directory.Exists(MainViewModel.PageSnapshotFolder))
            {
                Directory.Delete(MainViewModel.PageSnapshotFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>%LOCALAPPDATA%\YRK\EasyPdf\logs, for diagnosing problems users report.</summary>
    public static string LogFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "logs");

    private static void LogError(Exception exception) => LogMessage(exception.ToString());

    public static void LogMessage(string message)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            // Invariant culture: with Thai regional settings the year would otherwise be Buddhist (2569).
            var now = DateTime.Now;
            File.AppendAllText(
                Path.Combine(LogFolder, $"errors-{now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log"),
                $"[{now.ToString("O", CultureInfo.InvariantCulture)}] {message}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never make things worse.
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
