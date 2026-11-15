using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Ocr;

namespace Yrk.PdfStudio.App.ViewModels;

public enum OcrReviewChoice { Save, CancelFile, SkipRemaining }

/// <summary>A review target always refers to the original, unfiltered Words list.</summary>
public sealed record OcrReviewWord(int PageIndex, int WordIndex, OcrWord Original);

public sealed class OcrReviewViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPdfDocument _document; // Owned by the caller, kept open until DisposeAsync completes.
    private readonly Dictionary<int, Dictionary<int, string?>> _edits = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _renders = [];
    private int _selected = -1;
    private int _pagePosition;
    private string _editText = string.Empty;
    private bool _isEditing;
    private double _zoom = 1;
    private BitmapSource? _image;
    private int _renderVersion;

    public OcrReviewViewModel(IReadOnlyList<OcrPageResult> pages, IPdfDocument document, bool hasMoreFiles)
    {
        _document = document;
        HasMoreFiles = hasMoreFiles;
        Words = pages.OrderBy(p => p.PageIndex).SelectMany(p => OcrPageText.GetUncertainWordIndices(p).Order()
            .Select(i => new OcrReviewWord(p.PageIndex, i, p.Words[i]))).ToArray();
        PageIndices = Words.Select(w => w.PageIndex).Distinct().ToArray();
    }

    public IReadOnlyList<OcrReviewWord> Words { get; }
    public IReadOnlyList<int> PageIndices { get; }
    public bool HasMoreFiles { get; }
    public int PageIndex => PageIndices.Count == 0 ? 0 : PageIndices[_pagePosition];
    public string PageText => Loc.F("Page {0} of {1}", PageIndex + 1, _document.PageCount);
    public int Remaining => Words.Count(w => !IsConfirmed(w));
    public string RemainingText => Loc.F("{0} words remaining", Remaining);
    public OcrReviewWord? SelectedWord => _selected < 0 ? null : Words[_selected];
    public bool IsEditing { get => _isEditing; private set => SetProperty(ref _isEditing, value); }
    public string EditText { get => _editText; set => SetProperty(ref _editText, value); }
    public BitmapSource? Image { get => _image; private set => SetProperty(ref _image, value); }
    public double PageWidth => _document.PageSizes[PageIndex].Width * _zoom;
    public double PageHeight => _document.PageSizes[PageIndex].Height * _zoom;
    public double Zoom => _zoom;
    public string Error { get; private set; } = string.Empty;

    public bool IsConfirmed(OcrReviewWord word) => _edits.TryGetValue(word.PageIndex, out var edits) && edits.ContainsKey(word.WordIndex);
    public string WordText(OcrReviewWord word) => _edits.TryGetValue(word.PageIndex, out var edits)
        && edits.TryGetValue(word.WordIndex, out var text) ? text ?? string.Empty : word.Original.Text;
    public IReadOnlyDictionary<int, string?> EditsForPage(int pageIndex) => _edits.TryGetValue(pageIndex, out var edits)
        ? new System.Collections.ObjectModel.ReadOnlyDictionary<int, string?>(edits)
        : System.Collections.Immutable.ImmutableDictionary<int, string?>.Empty;

    public void SelectWord(OcrReviewWord word)
    {
        var index = Words.ToList().IndexOf(word);
        if (index < 0) throw new ArgumentException(nameof(word));
        _selected = index;
        _pagePosition = PageIndices.ToList().IndexOf(word.PageIndex);
        EditText = WordText(word);
        IsEditing = true;
        Changed();
    }

    public void NextWord(int direction = 1, bool onlyRemaining = false)
    {
        if (Words.Count == 0) return;
        var start = _selected;
        if (start < 0)
        {
            var indices = Enumerable.Range(0, Words.Count).Where(i => Words[i].PageIndex == PageIndex);
            start = direction > 0 ? indices.First() - 1 : indices.Last() + 1;
        }
        for (var step = 1; step <= Words.Count; step++)
        {
            var index = (start + direction * step % Words.Count + Words.Count) % Words.Count;
            if (onlyRemaining && IsConfirmed(Words[index])) continue;
            SelectWord(Words[index]);
            return;
        }
        CancelEdit();
    }

    public void ConfirmEdit(bool next = true)
    {
        if (!IsEditing || SelectedWord is not { } word) return;
        if (!_edits.TryGetValue(word.PageIndex, out var edits)) _edits[word.PageIndex] = edits = [];
        edits[word.WordIndex] = string.IsNullOrWhiteSpace(EditText) ? null : EditText;
        IsEditing = false;
        Changed();
        if (next) NextWord(onlyRemaining: true);
    }

    public void DeleteWord() { if (IsEditing) { EditText = string.Empty; ConfirmEdit(); } }
    public void CancelEdit() { IsEditing = false; EditText = SelectedWord is { } w ? WordText(w) : string.Empty; Changed(); }
    public void MovePage(int direction)
    {
        CancelEdit();
        _pagePosition = Math.Clamp(_pagePosition + direction, 0, Math.Max(0, PageIndices.Count - 1));
        _selected = -1;
        Changed();
    }
    public void ChangeZoom(double factor) { _zoom = Math.Clamp(_zoom * factor, .5, 4); Changed(); }

    private void Changed()
    {
        foreach (var name in new[] { nameof(SelectedWord), nameof(PageIndex), nameof(PageText), nameof(Remaining),
            nameof(RemainingText), nameof(PageWidth), nameof(PageHeight), nameof(Zoom) }) OnPropertyChanged(name);
        ReviewChanged?.Invoke(this, EventArgs.Empty);
    }
    public event EventHandler? ReviewChanged;

    // Track every render so closing the modal cannot dispose its document during an in-flight render.
    public Task RenderAsync()
    {
        if (_lifetime.IsCancellationRequested) return Task.CompletedTask;
        Image = null;
        _renders.RemoveAll(t => t.IsCompletedSuccessfully);
        var task = RenderCoreAsync(++_renderVersion, PageIndex, PageWidth, PageHeight);
        _renders.Add(task);
        return task;
    }

    private async Task RenderCoreAsync(int version, int page, double width, double height)
    {
        try
        {
            var scale = Math.Min(1.5, Math.Min(7000 / Math.Max(width, height), Math.Sqrt(20_000_000 / (width * height))));
            var bitmap = await _document.RenderPageAsync(page, Math.Max(1, (int)Math.Ceiling(width * scale)),
                Math.Max(1, (int)Math.Ceiling(height * scale)), cancellationToken: _lifetime.Token);
            if (!_lifetime.IsCancellationRequested && version == _renderVersion)
            {
                Image = BitmapFactory.Create(bitmap);
                Error = string.Empty;
                OnPropertyChanged(nameof(Error));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is PdfException or InvalidOperationException or ArgumentException)
        {
            if (!_lifetime.IsCancellationRequested && version == _renderVersion)
            {
                var error = ErrorCatalog.OcrReviewRender;
                App.LogError(error, ex);
                Error = Loc.T(error.Message) + "\n" + Loc.T(error.NextStep);
                OnPropertyChanged(nameof(Error));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { await Task.WhenAll(_renders); }
        finally { _lifetime.Dispose(); }
    }
}
