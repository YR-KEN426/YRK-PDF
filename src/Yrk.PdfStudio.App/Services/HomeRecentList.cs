using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using Yrk.PdfStudio.App.Localization;

namespace Yrk.PdfStudio.App.Services;

/// <summary>What kind of file a line is, for the colours of its badge.</summary>
public enum RecentKind { Pdf, Cad, Other }

/// <summary>One line of the recent files panel on the start page.</summary>
public sealed class RecentRow(RecentFile file, string timeText)
{
    public string Path { get; } = file.Path;

    public string Name { get; } = file.Name;

    /// <summary>The extension in capitals for the little badge (PDF, DWG, DXF, AI).</summary>
    public string Badge { get; } = BadgeFor(file.Path);

    /// <summary>Picks the badge colours.</summary>
    public RecentKind Kind { get; } = KindFor(file.Path);

    /// <summary>When it was opened last ("Today 10:42", "Yesterday", "3 Nov"), empty when not known.</summary>
    public string TimeText { get; } = timeText;

    public bool HasTime => TimeText.Length > 0;

    private static string BadgeFor(string path)
    {
        var extension = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        return extension.Length == 0 ? NoExtension : extension.Length > 4 ? extension[..4] : extension;
    }

    private const string NoExtension = "FILE";

    private static RecentKind KindFor(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => RecentKind.Pdf,
        ".dwg" or ".dxf" => RecentKind.Cad,
        _ => RecentKind.Other,
    };
}

/// <summary>
/// What the start page shows as "Recent files": the files of <see cref="RecentFiles"/> that still exist, 6 of them or all 12.
/// <list type="bullet">
/// <item>A file that cannot be found is hidden only. It stays in the history, because a drive that was unplugged or a network
/// that dropped does not mean the file is gone. A path on the network (UNC or a mapped drive) is not looked for at all, since that
/// would contact the network, and is always shown.</item>
/// <item>The looking is done off the UI thread. A result that arrives after the list was cleared, a file was removed or the option
/// was turned off is thrown away and the list is looked at again, so nothing comes back that should not.</item>
/// </list>
/// </summary>
public sealed class HomeRecentList : INotifyPropertyChanged, IDisposable
{
    public const int CollapsedRows = 6;

    private readonly Func<string, bool> _exists;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;
    private readonly object _gate = new();
    private readonly List<RecentFile> _found = [];
    private ObservableCollection<RecentFile>? _source;
    private bool _enabled = true;
    private bool _expanded;
    private bool _again;
    private bool _running;
    private int _generation;
    private Task _current = Task.CompletedTask;

    /// <param name="exists">Whether a file is there; runs on a thread-pool thread. Default: <see cref="FileIsThere"/>.</param>
    /// <param name="dispatcher">The UI thread: a finished look changes the list there. Null runs it where it finishes (tests).</param>
    public HomeRecentList(Func<string, bool>? exists = null, System.Windows.Threading.Dispatcher? dispatcher = null)
    {
        _exists = exists ?? FileIsThere;
        _dispatcher = dispatcher;
        Loc.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<RecentRow> Rows { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The panel has something to show (the right-hand column exists only then).</summary>
    public bool HasRows => _enabled && !_suppressed && Rows.Count > 0;

    private bool _suppressed;

    /// <summary>True while the start page shows search results: the panel steps aside without losing its rows.</summary>
    public bool Suppressed
    {
        get => _suppressed;
        set
        {
            if (_suppressed == value) return;
            _suppressed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRows)));
        }
    }

    /// <summary>More than 6 files exist, so "View all" is offered.</summary>
    public bool CanExpand => _enabled && _found.Count > CollapsedRows;

    public bool IsExpanded => _expanded;

    public string ToggleText => Loc.T(_expanded ? "Show fewer" : "View all");

    /// <summary>The setting "Show recent files on the start page". Off hides the panel; the history is still kept.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            lock (_gate)
            {
                if (_enabled == value) return;
                _enabled = value;
                _generation++;
            }

            if (!value) Rebuild();
            _ = RefreshAsync();
        }
    }

    /// <summary>"View all" / "Show fewer".</summary>
    public void ToggleExpanded()
    {
        _expanded = !_expanded;
        Rebuild();
    }

    /// <summary>Looks at the files again. The task ends when the list has settled (more than one look may have been needed).</summary>
    public Task RefreshAsync()
    {
        Attach();
        lock (_gate)
        {
            _again = true;
            if (_running) return _current;
            _running = true;
        }

        return _current = RunAsync();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            int generation;
            bool enabled;
            lock (_gate)
            {
                if (!_again)
                {
                    _running = false;
                    return;
                }

                _again = false;
                generation = _generation;
                enabled = _enabled;
            }

            if (!enabled)
            {
                await OnUiThread(() => SetFound([]));
                continue;
            }

            var snapshot = RecentFiles.Items.ToList();
            var checkedFiles = await Task.Run(() => snapshot.Where(f => _exists(f.Path)).ToList());
            lock (_gate)
            {
                // Something changed while we were looking: this answer is old, so it is not used.
                if (generation != _generation || _again) continue;
            }

            await OnUiThread(() => SetFound(checkedFiles));
        }
    }

    private Task OnUiThread(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    private void Attach()
    {
        var items = RecentFiles.Items;
        if (ReferenceEquals(items, _source)) return;
        if (_source is not null) _source.CollectionChanged -= OnItemsChanged;
        _source = items;
        _source.CollectionChanged += OnItemsChanged;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        lock (_gate) _generation++;
        // A file taken out of the list leaves the panel at once; a new one appears after it has been looked at.
        var current = RecentFiles.Items;
        _found.RemoveAll(found => !current.Any(item => SamePath(item.Path, found.Path)));
        Rebuild();
        _ = RefreshAsync();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Rebuild();
    }

    private void SetFound(List<RecentFile> files)
    {
        _found.Clear();
        _found.AddRange(files.Take(RecentFiles.MaxCount));
        Rebuild();
    }

    private void Rebuild()
    {
        var shown = _enabled ? _found.Take(_expanded ? RecentFiles.MaxCount : CollapsedRows).ToList() : [];
        var now = RecentFiles.UtcNow;
        var rows = shown.Select(f => new RecentRow(f, RecentTimeText.Format(f.LastOpened, now))).ToList();
        if (!rows.Select(r => (r.Path, r.TimeText)).SequenceEqual(Rows.Select(r => (r.Path, r.TimeText))))
        {
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
        }

        foreach (var name in new[] { nameof(HasRows), nameof(CanExpand), nameof(IsExpanded), nameof(ToggleText) })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        if (_source is not null) _source.CollectionChanged -= OnItemsChanged;
        _source = null;
    }

    /// <summary>
    /// Whether the file is there. Paths on the network are not looked for (that would contact the network) and count as there.
    /// </summary>
    internal static bool FileIsThere(string path)
    {
        if (IsNetworkPath(path)) return true;
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true; // Cannot tell: do not hide it.
        }
    }

    /// <summary>A UNC path (\\server\share) or a path on a mapped network drive. Asking the drive type does not contact the network.</summary>
    internal static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\?\C:\folder is a local path in the long form; \\?\UNC\server and \\server are the network.
            return !(path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length >= 7 && path[5] == ':'
                     && char.IsLetter(path[4]));
        }

        try
        {
            var root = System.IO.Path.GetPathRoot(path);
            return root is { Length: >= 2 } && root[1] == ':' && char.IsLetter(root[0])
                && new DriveInfo(root[..1]).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>How the time a file was last opened is written in the list: "Today 10:42", "Yesterday", "3 Nov".</summary>
public static class RecentTimeText
{
    private const string ClockFormat = "HH:mm";
    private const string DayMonthFormat = "d MMM";
    private const string DayMonthYearFormat = "d MMM yyyy";

    public static string Format(DateTimeOffset? opened, DateTimeOffset now)
    {
        if (opened is not { } time) return string.Empty;
        var local = time.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var culture = Loc.IsThai ? CultureInfo.GetCultureInfo("th-TH") : CultureInfo.GetCultureInfo("en-GB");
        var days = (today - local.Date).Days;
        if (days <= 0) return Loc.F("Today {0}", local.ToString(ClockFormat, culture));
        if (days == 1) return Loc.T("Yesterday");
        return local.ToString(local.Year == today.Year ? DayMonthFormat : DayMonthYearFormat, culture);
    }
}
