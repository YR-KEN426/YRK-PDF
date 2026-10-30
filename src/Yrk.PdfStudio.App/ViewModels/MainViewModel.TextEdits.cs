using System.Windows.Media;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// Editing existing text: click a line, type, and the line is replaced when the document is saved (its
/// characters are taken out of the page and the new text is written as real, searchable text).
/// </summary>
public sealed partial class MainViewModel
{
    private readonly List<TextEditViewModel> _textEdits = [];
    private static readonly Lazy<Dictionary<string, string>> InstalledFamilies = new(() =>
        Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .GroupBy(Normalize, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal));

    public bool HasTextEdits => _textEdits.Any(e => e.IsChanged);

    private async Task BeginTextEditAsync(PageViewModel page, NormalizedPoint point)
    {
        if (_document is not { } document)
        {
            return;
        }

        if (_textEdits.FirstOrDefault(e => e.Page == page && e.Contains(point)) is { } existing)
        {
            ActiveTextEdit = existing;
            existing.IsEditing = true;
            return;
        }

        PdfTextLine? line;
        try
        {
            line = await document.GetTextLineAtAsync(page.Index, point);
        }
        catch (PdfException)
        {
            line = null;
        }

        if (line is null || line.Text.Length == 0)
        {
            StatusMessage = "Click on a line of text to edit it";
            return;
        }

        // The document's own font when it is installed; otherwise a substitute, sized to look as tall.
        var family = ResolveFamily(line.FontName, out var exact);
        var size = exact ? line.FontSize : MatchSize(line, family, page.HeightPoints);
        var edit = new TextEditViewModel(page, line, family, size);
        AttachTextEdit(edit);
        ActiveTextEdit = edit;
        edit.IsEditing = true;
        StatusMessage = exact
            ? "Edit the text, then press Enter or click outside it."
            : Loc.F("The document's font {0} is not installed: {1} is used instead. Change the font or size on the toolbar if needed.", line.FontName, family);
    }

    /// <summary>
    /// A size for a substitute font that makes the line as tall on the page as the original: fonts differ a
    /// lot at the same point size (Thai ones especially), so the original line's ink height is matched.
    /// </summary>
    internal static double MatchSize(PdfTextLine line, string family, double pageHeight)
    {
        var target = line.Bounds.Height * pageHeight;
        if (target <= 0 || line.FontSize <= 0)
        {
            return line.FontSize;
        }

        var typeface = new Typeface(
            new FontFamily(BundledFonts.Locate(family)),
            line.IsItalic ? System.Windows.FontStyles.Italic : System.Windows.FontStyles.Normal,
            line.IsBold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal,
            System.Windows.FontStretches.Normal);
        var text = new FormattedText(
            line.Text, System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            typeface, line.FontSize, Brushes.Black, 1.0);
        var ink = text.BuildGeometry(new System.Windows.Point()).Bounds;
        if (ink.IsEmpty || ink.Height <= 0)
        {
            return line.FontSize;
        }

        var ratio = Math.Clamp(target / ink.Height, 0.5, 2);
        return Math.Abs(ratio - 1) < 0.05 ? line.FontSize : Math.Round(line.FontSize * ratio * 2) / 2;
    }

    // --- The line the font bar styles on the Edit tab -------------------------------------------

    private TextEditViewModel? _activeTextEdit;

    /// <summary>The line the font bar applies to on the Edit tab: the one being edited, or the last one.</summary>
    public TextEditViewModel? ActiveTextEdit
    {
        get => _activeTextEdit;
        private set
        {
            if (SetProperty(ref _activeTextEdit, value))
            {
                OnPropertyChanged(nameof(HasActiveTextEdit));
                UpdateTextStyleTarget();
            }
        }
    }

    public bool HasActiveTextEdit => _activeTextEdit is not null;

    /// <summary>A line whose look changed is kept (even with its text unchanged) and marks the document changed.</summary>
    private void StyleChanged(TextEditViewModel edit)
    {
        if (!_textEdits.Contains(edit))
        {
            AttachTextEdit(edit);
        }

        OnEditsChanged();
    }

    /// <summary>
    /// Called by the view when editing a line ends (Enter or focus leaves); Escape passes <paramref name="cancel"/>.
    /// Enter passes <paramref name="apply"/>: the changed lines are written into the page now. When the focus only
    /// moves (to the font bar, say) the line stays a preview until a click outside it or another tool.
    /// </summary>
    public void EndTextEdit(TextEditViewModel edit, string text, bool cancel, bool apply = false)
    {
        KeepTextEdit(edit, text, cancel);
        if (apply)
        {
            _ = ApplyTextEditsNowAsync();
        }
    }

    private void KeepTextEdit(TextEditViewModel edit, string text, bool cancel)
    {
        edit.IsEditing = false;
        var before = edit.Text;
        var after = cancel ? before : text.Trim();
        if (after == before)
        {
            if (!edit.IsChanged)
            {
                DetachTextEdit(edit); // Opened but not changed: nothing to keep.
            }

            return;
        }

        edit.Text = after;
        _undo.Push(
            () =>
            {
                edit.Text = before;
                if (!edit.IsChanged)
                {
                    DetachTextEdit(edit);
                }

                OnEditsChanged();
            },
            () =>
            {
                if (!_textEdits.Contains(edit))
                {
                    AttachTextEdit(edit);
                }

                edit.Text = after;
                OnEditsChanged();
            });
        OnEditsChanged();
    }

    /// <summary>Writes the changed lines into the page now (see <see cref="ApplyNowAsync"/>); Undo brings the old text back.</summary>
    private Task ApplyTextEditsNowAsync() =>
        ApplyNowAsync("Changing the text…", "Text changed. Undo (Ctrl+Z) brings the old text back.", "Couldn't change the text.\n\n{0}", () => HasTextEdits);

    /// <summary>Applies the changed lines to saved document bytes (used when saving).</summary>
    /// <exception cref="PdfException">A line cannot be edited.</exception>
    private byte[] ApplyTextEdits(byte[] pdf)
    {
        var changed = _textEdits.Where(e => e.IsChanged).ToList();
        if (changed.Count == 0)
        {
            return pdf;
        }

        var replaced = changed.Where(e => e.Text.Length > 0).ToList();
        IReadOnlyList<PdfTextBlock> blocks;
        try
        {
            blocks = TextShaper.ToBlocks(replaced.Select(e => Layout(e, e.FontFamilyName)).ToList());
        }
        catch (InvalidOperationException)
        {
            // A font that cannot be embedded: write everything in the bundled-with-Windows Thai/Latin font.
            blocks = TextShaper.ToBlocks(replaced.Select(e => Layout(e, TextShaper.ThaiFallbackFamily)).ToList());
        }

        var edits = changed.Select(e => new PdfTextEdit(
            e.Page.Index,
            e.Line.Bounds,
            replaced.IndexOf(e) is >= 0 and var i ? blocks[i] : null)).ToList();
        return PdfTextEditor.Apply(pdf, edits, _document?.OpenPassword);
    }

    /// <summary>The new text shaped and placed on the old line's baseline.</summary>
    private static (int PageIndex, NormalizedPoint TopLeft, PdfColor Color, TextShaper.ShapedLine Line) Layout(TextEditViewModel edit, string family)
    {
        var shaped = TextShaper.Shape(edit.Text, family, edit.IsBold, edit.IsItalic, edit.FontSize);
        var baseline = edit.Line.BaselineStart;
        var topLeft = new NormalizedPoint(baseline.X, baseline.Y - (shaped.Baseline / edit.Page.HeightPoints));
        return (edit.Page.Index, topLeft, new PdfColor(edit.Color.R, edit.Color.G, edit.Color.B), shaped);
    }

    private bool BlockedByTextEdits()
    {
        if (!HasTextEdits)
        {
            return false;
        }

        _dialogs.ShowInfo("Save your text changes first (Ctrl+S), then use the page tools.");
        return true;
    }

    private void AttachTextEdit(TextEditViewModel edit)
    {
        if (!Pages.Contains(edit.Page))
        {
            return;
        }

        _textEdits.Add(edit);
        edit.Page.TextEdits.Add(edit);
    }

    private void DetachTextEdit(TextEditViewModel edit)
    {
        _textEdits.Remove(edit);
        edit.Page.TextEdits.Remove(edit);
    }

    private void ClearTextEdits()
    {
        foreach (var edit in _textEdits.ToList())
        {
            DetachTextEdit(edit);
        }

        ActiveTextEdit = null;
    }

    /// <summary>
    /// The installed family for a PDF font name such as "ABCDEF+Sarabun-Bold", "TimesNewRomanPSMT" or
    /// "TH SarabunPSK"; otherwise a Windows font that covers Thai and Latin.
    /// </summary>
    internal static string ResolveFamily(string pdfFontName) => ResolveFamily(pdfFontName, out _);

    /// <param name="exact">False when the document's font is not installed and a substitute is returned.</param>
    internal static string ResolveFamily(string pdfFontName, out bool exact)
    {
        var name = Normalize(BaseName(pdfFontName));
        if (InstalledFamilies.Value.TryGetValue(name, out var found))
        {
            exact = true;
            return found;
        }

        // "Sarabun-Light" in a Word file: the Sarabun shipped with the program has the same shapes and sizes.
        exact = name == Normalize(BundledFonts.Sarabun) && BundledFonts.HasSarabun;
        return exact ? BundledFonts.Sarabun : TextShaper.ThaiFallbackFamily;
    }

    private static string BaseName(string pdfFontName)
    {
        var name = pdfFontName.Split('-', ',')[0];
        foreach (var suffix in new[] { "PSMT", "MT", "PS" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length + 2 && !InstalledFamilies.Value.ContainsKey(Normalize(name)))
            {
                name = name[..^suffix.Length];
            }
        }

        return name;
    }

    private static string Normalize(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
