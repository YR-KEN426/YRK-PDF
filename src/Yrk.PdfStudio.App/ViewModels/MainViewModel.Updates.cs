using CommunityToolkit.Mvvm.Input;
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

    /// <summary>Set by the window: asks about unsaved changes in every tab before a restart.</summary>
    public Func<Task<bool>>? ConfirmAllTabsAsync { get; set; }
    private static bool s_isCheckingForUpdates;
    private IAsyncRelayCommand? _restartToUpdateCommand;
    private IRelayCommand? _showAboutCommand;
    private IRelayCommand<string>? _setLanguageCommand;

    public bool IsUpdateReady => s_readyVersion is not null;

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
        if (!_updater.IsAvailable)
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
        if (_settings.CheckForUpdates) // It may have been switched off in the meantime.
        {
            await CheckForUpdatesAsync(quiet: true);
        }
    }

    /// <summary>Checks the feed and downloads an update. Returns a sentence for the user.</summary>
    private async Task<string> CheckForUpdatesAsync(bool quiet)
    {
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
        try
        {
            var update = await _updater.CheckAndDownloadAsync(percent =>
            {
                if (!quiet)
                {
                    App.Current?.Dispatcher.BeginInvoke(() => StatusMessage = Loc.F("Downloading the update… {0}%", percent));
                }
            });
            if (update is null)
            {
                return Loc.F("You have the latest version ({0}).", AppVersion);
            }

            s_readyVersion = update.TargetFullRelease.Version.ToString();
            UpdateReady?.Invoke(null, EventArgs.Empty);
            StatusMessage = string.Empty;
            return Loc.F("Version {0} is downloaded. Restart to install it.", s_readyVersion);
        }
        catch (UpdateVerificationException ex)
        {
            // Not signed with YRK's key: it was deleted. Say so, even for the background check.
            App.LogMessage(ex.Message);
            StatusMessage = "An update was found but it is not signed by YRK, so it was deleted and not installed.";
            return Loc.T("An update was found but it is not signed by YRK, so it was deleted and not installed.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Offline or a broken feed: never bother the user about it in the background.
            App.LogMessage("Update check failed: " + ex);
            return Loc.T("Couldn't check for updates. Check the internet connection and try again later.");
        }
        finally
        {
            s_isCheckingForUpdates = false;
        }
    }

    private async Task RestartToUpdateAsync()
    {
        if (s_readyVersion is null || IsBusy || !await (ConfirmAllTabsAsync ?? ConfirmDiscardOrSaveAsync)())
        {
            return;
        }

        _updater.ApplyAndRestart();
    }

    /// <summary>What the About dialog shows and can do.</summary>
    public sealed class AboutModel(MainViewModel owner)
    {
        public string Version => owner.AppVersion;

        public bool CanUpdate => owner._updater.IsAvailable;

        public string UpdateSource => owner._updater.IsAvailable
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

        public Task<string> CheckNowAsync() => owner.CheckForUpdatesAsync(quiet: false);

        public Task RestartToUpdateAsync() => owner.RestartToUpdateAsync();
    }
}
