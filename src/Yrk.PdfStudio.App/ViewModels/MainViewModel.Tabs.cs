using System.IO;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// Each open file is one <see cref="MainViewModel"/> shown as a tab; the window shows the active one.
/// These members are what the tab strip and the window need from a tab.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _isActiveTab;
    private EventHandler? _onLanguageChanged;

    /// <summary>The user chose a file to open while this tab already shows one: open it in a new tab.</summary>
    public event EventHandler<string>? OpenInNewTabRequested;

    /// <summary>Close was chosen (Ctrl+W): the window closes this tab.</summary>
    public event EventHandler? CloseTabRequested;

    /// <summary>File name for the tab, with a dot while there are unsaved changes.</summary>
    public string TabTitle => DisplayFileName is not { } name
        ? Loc.T("New tab")
        : (IsDirty ? "• " : string.Empty) + name;

    /// <summary>The file this tab shows (the drawing for converted DWG/DXF), to avoid opening it twice.</summary>
    public string? DocumentPath => _convertedFrom ?? _document?.FilePath;

    public bool IsActiveTab
    {
        get => _isActiveTab;
        set => SetProperty(ref _isActiveTab, value);
    }

    /// <summary>Where this tab was scrolled to, restored when it becomes active again.</summary>
    public double ScrollOffset => _scrollOffset;

    /// <summary>
    /// Closes the document and clears edits (after <see cref="ConfirmDiscardOrSaveAsync"/>).
    /// When <paramref name="keepAlive"/> is true, keeps event subscriptions for the tab that remains as Home.
    /// </summary>
    public async Task CloseTabAsync(bool keepAlive = false)
    {
        await ReleaseDocumentAsync();
        ClearEdits();
        if (!keepAlive && _onLanguageChanged is not null)
        {
            Loc.LanguageChanged -= _onLanguageChanged;
            UpdateReady -= _onUpdateReady;
        }
    }

    private EventHandler? _onUpdateReady;

    private void SubscribeToLanguage()
    {
        _onLanguageChanged = (_, _) =>
        {
            OnPropertyChanged(string.Empty); // Recompute every translated text.
            _selectedItem?.RefreshLanguage();
        };
        Loc.LanguageChanged += _onLanguageChanged;
        _onUpdateReady = (_, _) =>
        {
            OnPropertyChanged(nameof(IsUpdateReady));
            OnPropertyChanged(nameof(UpdateReadyText));
            OnPropertyChanged(nameof(ShowUpdateBanner));
            OnPropertyChanged(nameof(IsUpdateDownloading));
            OnPropertyChanged(nameof(IsUpdateStopped));
            OnPropertyChanged(nameof(UpdateDownloadPercent));
            OnPropertyChanged(nameof(UpdateBannerText));
            _retryUpdateCommand?.NotifyCanExecuteChanged();
        };
        UpdateReady += _onUpdateReady;
    }

    private bool RequestOpenInNewTab(string path)
    {
        if (_document is null || OpenInNewTabRequested is null)
        {
            return false;
        }

        OpenInNewTabRequested(this, path);
        return true;
    }

    private bool RequestCloseTab()
    {
        if (CloseTabRequested is null)
        {
            return false;
        }

        CloseTabRequested(this, EventArgs.Empty);
        return true;
    }
}
