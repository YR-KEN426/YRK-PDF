using CommunityToolkit.Mvvm.Input;
using System.Windows.Threading;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>Automatic updates and the About dialog.</summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan ChoiceDelay = TimeSpan.FromSeconds(2);

    /// <summary>One settings object for all tabs, so a change in one tab is not undone by another saving.</summary>
    private static readonly AppSettings SharedSettings = AppSettings.Load();

    /// <summary>One updater for all tabs: the tab that shows the button may not be the one that downloaded.</summary>
    private static readonly AppUpdater SharedUpdater = new();

    private AppUpdater _updater => SharedUpdater;

    private AppSettings _settings => SharedSettings;
    private static string? s_readyVersion; // Shared: every tab shows the same update banner.
    private static event EventHandler? UpdateReady;
    private enum UpdateState { None, Downloading, Stalled, Stopped, Ready }
    private static UpdateState s_updateState;
    private static readonly object UpdateProgressLock = new();
    private static UpdateDownloadProgress? s_downloadProgress;
    private static CancellationTokenSource? s_updateCancellation;
    private static DispatcherTimer? s_updateTimer;
    private static string? s_downloadingVersion;
    private static long s_downloadSize;
    private static DateTimeOffset s_lastProgressUi;
    private static bool s_progressUiPending;
    private static bool s_autoDownloadSuppressed;
    private IRelayCommand? _cancelUpdateCommand;
    private IAsyncRelayCommand? _retryUpdateCommand;
    private IRelayCommand? _dismissUpdateCommand;

    /// <summary>Set by the window: asks about unsaved changes in every tab before a restart.</summary>
    public Func<Task<bool>>? ConfirmAllTabsAsync { get; set; }
    private static bool s_isCheckingForUpdates;
    private IAsyncRelayCommand? _restartToUpdateCommand;
    private IRelayCommand? _showAboutCommand;
    private IRelayCommand<string>? _setLanguageCommand;

    public bool IsUpdateReady => s_readyVersion is not null;
    public bool ShowUpdateBanner => s_updateState != UpdateState.None;
    public bool IsUpdateDownloading => s_updateState is UpdateState.Downloading or UpdateState.Stalled;
    public bool IsUpdateStopped => s_updateState == UpdateState.Stopped;
    public int UpdateDownloadPercent { get { lock (UpdateProgressLock) return s_downloadProgress?.Percent ?? 0; } }
    public string UpdateBannerText
    {
        get
        {
            lock (UpdateProgressLock)
            {
                return s_updateState switch
                {
                    UpdateState.Ready => UpdateReadyText,
                    UpdateState.Stalled => Loc.T("The download isn't progressing. The connection may be slow or stalled."),
                    UpdateState.Stopped => Loc.T("The download has stopped."),
                    UpdateState.Downloading => Loc.F("Downloading YRK PDF {0}… {1}% ({2} / {3} MB)",
                        s_downloadingVersion, s_downloadProgress?.Percent ?? 0,
                        ((s_downloadProgress?.DownloadedBytes ?? 0) / 1048576d).ToString("F1"),
                        (s_downloadSize / 1048576d).ToString("F1")),
                    _ => string.Empty
                };
            }
        }
    }

    public IRelayCommand CancelUpdateCommand => _cancelUpdateCommand ??= new RelayCommand(() =>
    {
        if (!IsUpdateDownloading) return;
        s_autoDownloadSuppressed = true; // A cancelled download must wait for an explicit retry or manual check.
        // Keep the source alive until the updater has finished cancellation and cleanup.
        SetUpdateState(UpdateState.Stopped);
        s_updateTimer?.Stop();
        s_updateCancellation?.Cancel();
    });
    public IAsyncRelayCommand RetryUpdateCommand => _retryUpdateCommand ??= new AsyncRelayCommand(
        async () => { await CheckForUpdatesAsync(quiet: true); },
        () => IsUpdateStopped && !s_isCheckingForUpdates);
    public IRelayCommand DismissUpdateCommand => _dismissUpdateCommand ??= new RelayCommand(() =>
    {
        if (IsUpdateStopped) SetUpdateState(UpdateState.None);
    });

    private static void SetUpdateState(UpdateState state)
    {
        s_updateState = state;
        UpdateReady?.Invoke(null, EventArgs.Empty);
    }

    public string UpdateReadyText => Loc.F("YRK PDF {0} is ready to install.", s_readyVersion);

    public string AppVersion => _updater.CurrentVersion;

    public IAsyncRelayCommand RestartToUpdateCommand => _restartToUpdateCommand ??= new AsyncRelayCommand(RestartToUpdateAsync);

    public IRelayCommand ShowAboutCommand => _showAboutCommand ??= new RelayCommand(() => _dialogs.ShowAbout(new AboutModel(this)));

    public IRelayCommand ShowPrivacyCommand => _showPrivacyCommand ??= new RelayCommand(() => _dialogs.ShowPrivacy(new AboutModel(this)));

    private IRelayCommand? _showPrivacyCommand;

    public IRelayCommand ShowFeedbackCommand => _showFeedbackCommand ??= new RelayCommand(() => _dialogs.ShowFeedback(AppVersion));

    private IRelayCommand? _showFeedbackCommand;

    /// <summary>Switches the menus to "en" or "th" at once and remembers the choice.</summary>
    public IRelayCommand<string> SetLanguageCommand => _setLanguageCommand ??= new RelayCommand<string>(language =>
    {
        if (language is Loc.English or Loc.Thai)
        {
            Loc.Language = language;
            _settings.Language = language;
            _settings.Save();
        }
    });

    /// <summary>
    /// Called once the window is up. The first time, the user chooses whether the app may check for updates;
    /// nothing goes out before that. Then a quiet check downloads a newer version in the background.
    /// </summary>
    public async Task CheckForUpdatesInBackgroundAsync()
    {
        if (s_autoDownloadSuppressed || !_updater.IsAvailable)
        {
            return;
        }

        if (!_settings.UpdateChoiceMade)
        {
            await Task.Delay(ChoiceDelay); // Let the window finish appearing first.
            _settings.CheckForUpdates = _dialogs.AskYesNo(
                "Check for updates automatically?\n\nShortly after starting, YRK PDF would ask its download page on GitHub whether a newer version exists and download it in the background. It is installed only when you click \"Restart to update\". Nothing about you or your files is sent.\n\nYou can change this any time in Settings ▸ Privacy and safety.");
            _settings.UpdateChoiceMade = true;
            _settings.Save();
        }

        if (!_settings.CheckForUpdates)
        {
            return;
        }

        await Task.Delay(FirstCheckDelay);
        if (!s_autoDownloadSuppressed && _settings.CheckForUpdates) // It may have been switched off in the meantime.
        {
            await CheckForUpdatesAsync(quiet: true);
        }
    }

    /// <summary>Checks the feed and downloads an update. Returns a sentence for the user.</summary>
    private async Task<string> CheckForUpdatesAsync(bool quiet, Action<int>? progress = null)
    {
        var dispatcher = App.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
            return await dispatcher.InvokeAsync(() => CheckForUpdatesAsync(quiet, progress)).Task.Unwrap();

        if (!_updater.IsAvailable)
        {
            return string.IsNullOrWhiteSpace(_updater.FeedUrl)
                ? Loc.T("Updates are not set up in this build.")
                : Loc.T("Updates work in the installed (or portable) app, not in a development build.");
        }

        if (s_isCheckingForUpdates)
        {
            return Loc.T("Already checking for updates…");
        }

        if (s_readyVersion is not null)
        {
            return Loc.F("Version {0} is downloaded. Restart to install it.", s_readyVersion);
        }

        s_isCheckingForUpdates = true;
        var cancellation = new CancellationTokenSource();
        s_updateCancellation = cancellation;
        SetUpdateState(UpdateState.None); // Checking the feed alone does not show a banner.
        try
        {
            var update = await _updater.CheckAndDownloadAsync(percent =>
            {
                progress?.Invoke(percent);
                lock (UpdateProgressLock)
                {
                    if (s_updateCancellation != cancellation || cancellation.IsCancellationRequested) return;
                    var now = DateTimeOffset.UtcNow;
                    s_downloadProgress?.Report(percent, now);
                    if (s_progressUiPending || now - s_lastProgressUi < TimeSpan.FromMilliseconds(250)) return;
                    s_lastProgressUi = now;
                    s_progressUiPending = true;
                }
                dispatcher.BeginInvoke(() =>
                {
                    lock (UpdateProgressLock)
                    {
                        if (s_updateCancellation != cancellation) return;
                        s_progressUiPending = false;
                        s_lastProgressUi = DateTimeOffset.UtcNow;
                    }
                    if (s_updateCancellation != cancellation || !IsUpdateDownloading) return;
                    RefreshDownloadState(progressChanged: true);
                    if (!quiet)
                    {
                        StatusMessage = Loc.F("Downloading the update… {0}%", UpdateDownloadPercent);
                    }
                });
            }, cancellation.Token, (size, version) => dispatcher.Invoke(() =>
            {
                if (cancellation.IsCancellationRequested) return;
                lock (UpdateProgressLock)
                {
                    s_downloadSize = size;
                    s_downloadingVersion = version;
                    s_downloadProgress = new UpdateDownloadProgress(size);
                    s_downloadProgress.Report(0, DateTimeOffset.UtcNow);
                    s_lastProgressUi = DateTimeOffset.MinValue;
                    s_progressUiPending = false;
                }
                SetUpdateState(UpdateState.Downloading);
                s_updateTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
                    (_, _) => RefreshDownloadState(), dispatcher);
                s_updateTimer.Start();
            }));
            if (update is null)
            {
                SetUpdateState(UpdateState.None);
                return Loc.F("You have the latest version ({0}).", AppVersion);
            }

            s_readyVersion = update.TargetFullRelease.Version.ToString();
            SetUpdateState(UpdateState.Ready); // A completed, verified result wins a late cancel.
            StatusMessage = string.Empty;
            return Loc.F("Version {0} is downloaded. Restart to install it.", s_readyVersion);
        }
        catch (Exception) when (cancellation.IsCancellationRequested) // The updater may wrap the cancel in UpdateVerificationException.
        {
            if (s_updateState != UpdateState.None) SetUpdateState(UpdateState.Stopped);
            return Loc.T("The download has stopped.");
        }
        catch (TimeoutException ex)
        {
            SetUpdateState(UpdateState.Stopped);
            App.LogMessage("Update timed out: " + ex);
            StatusMessage = Loc.T("The download stopped. Check the internet connection and try again.");
            return StatusMessage;
        }
        catch (UpdateVerificationException ex)
        {
            SetUpdateState(UpdateState.None);
            // Not signed with YRK's key: it was deleted. Say so, even for the background check.
            App.LogMessage(ex.Message);
            StatusMessage = "An update was found but it is not signed by YRK, so it was deleted and not installed.";
            return Loc.T("An update was found but it is not signed by YRK, so it was deleted and not installed.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetUpdateState(UpdateState.None);
            // Offline or a broken feed: never bother the user about it in the background.
            App.LogMessage("Update check failed: " + ex);
            return Loc.T("Couldn't check for updates. Check the internet connection and try again later.");
        }
        finally
        {
            s_updateTimer?.Stop();
            s_updateTimer = null;
            lock (UpdateProgressLock) s_updateCancellation = null;
            cancellation.Dispose();
            s_isCheckingForUpdates = false;
            UpdateReady?.Invoke(null, EventArgs.Empty);
        }
    }

    private static void RefreshDownloadState(bool progressChanged = false)
    {
        if (s_updateState is not (UpdateState.Downloading or UpdateState.Stalled)) return;
        bool stalled;
        lock (UpdateProgressLock)
            stalled = s_downloadProgress?.IsStalled(DateTimeOffset.UtcNow) == true;
        var state = stalled ? UpdateState.Stalled : UpdateState.Downloading;
        if (progressChanged || state != s_updateState) SetUpdateState(state);
    }

    private async Task RestartToUpdateAsync()
    {
        if (s_readyVersion is null || IsBusy || !await (ConfirmAllTabsAsync ?? ConfirmDiscardOrSaveAsync)())
        {
            return;
        }

        try
        {
            await _updater.ApplyAndRestartAsync();
        }
        catch (UpdateVerificationException ex)
        {
            ReportUpdateApplyFailure(ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportUpdateApplyFailure(ex);
        }
    }

    private void ReportUpdateApplyFailure(Exception ex)
    {
        App.LogMessage(ex.ToString());
        s_readyVersion = null;
        SetUpdateState(UpdateState.None);
        StatusMessage = Loc.T("An update was found but it is not signed by YRK, so it was deleted and not installed.");
    }

    /// <summary>What the About dialog shows and can do.</summary>
    public sealed class AboutModel(MainViewModel owner)
    {
        public string Version => owner.AppVersion;

        public bool CanUpdate => owner._updater.IsAvailable;

        public bool UsesStoreUpdates => PackageIdentity.Current.UsesStoreUpdates;

        public string UpdateSource => UsesStoreUpdates
            ? Loc.T("Updates are managed through Microsoft Store.")
            : owner._updater.IsAvailable
            ? Loc.T(owner._updater.IsPortable ? "Portable copy · updates from " : "Updates from ") + owner._updater.FeedUrl
            : Loc.T(string.IsNullOrWhiteSpace(owner._updater.FeedUrl) ? "Updates are not set up in this build." : "Development build: updates are off.");

        public bool CheckAutomatically
        {
            get => owner._settings.CheckForUpdates;
            set
            {
                owner._settings.CheckForUpdates = value;
                owner._settings.Save();
            }
        }

        public bool IsUpdateReady => owner.IsUpdateReady;

        public string UpdateReadyText => owner.UpdateReadyText;

        public Task<string> CheckNowAsync(Action<int>? progress = null) => owner.CheckForUpdatesAsync(quiet: false, progress);

        public Task RestartToUpdateAsync() => owner.RestartToUpdateAsync();
    }
}
