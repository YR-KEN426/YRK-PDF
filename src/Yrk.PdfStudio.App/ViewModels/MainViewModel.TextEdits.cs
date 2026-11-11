using System.Windows;
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
    private sealed record TextEditFrame(int PageIndex, NormalizedRect CurrentBounds, NormalizedRect Bounds,
        double BaselineX, TextAlignment Alignment, string LeadingWhitespace);
    private List<TextEditFrame> _textEditFrames = [];

    private List<TextEditFrame> CaptureTextEditFrames()
    {
        var frames = _textEditFrames.ToList();
        foreach (var edit in _textEdits.Where(e => e.IsChanged))
        {
            frames.RemoveAll(f => f.PageIndex == edit.Page.Index && f.Bounds == edit.AlignmentBounds);
            if (string.IsNullOrWhiteSpace(edit.Text)) continue;
            var layout = Layout(edit, edit.FontFamilyName);
            var ink = TextEditInkBounds(layout.Line);
            var bounds = new NormalizedRect(layout.TopLeft.X + ink.X / edit.Page.WidthPoints,
                layout.TopLeft.Y + ink.Y / edit.Page.HeightPoints,
                ink.Width / edit.Page.WidthPoints, ink.Height / edit.Page.HeightPoints);
            var leading = edit.Text[..(edit.Text.Length - edit.Text.TrimStart().Length)];
            frames.Add(new(edit.Page.Index, bounds, edit.AlignmentBounds, edit.AlignmentBaselineX, edit.Alignment, leading));
        }
        return frames;
    }
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
        var frame = _textEditFrames.FirstOrDefault(f => f.PageIndex == page.Index
            && Math.Abs(f.CurrentBounds.X - line.Bounds.X) * page.WidthPoints < 1
            && Math.Abs(f.CurrentBounds.Y - line.Bounds.Y) * page.HeightPoints < 1
            && Math.Abs(f.CurrentBounds.Width - line.Bounds.Width) * page.WidthPoints < 1);
        var edit = new TextEditViewModel(page, line, family, size, frame?.LeadingWhitespace);
        if (frame is not null)
        {
            edit.AlignmentBounds = frame.Bounds;
            edit.AlignmentBaselineX = frame.BaselineX;
            edit.RestoreAlignment(frame.Alignment);
        }
        AttachTextEdit(edit);
        ActiveTextEdit = edit;
        edit.IsEditing = true;
        // The Edit tab's toolbar already says how to finish; the status line only reports a font substitute.
        _textEditStatus = exact
            ? string.Empty
            : Loc.F("The document's font {0} is not installed: {1} is used instead. Change the font or size on the toolbar if needed.", line.FontName, family);
        StatusMessage = _textEditStatus;
    }

    private string _textEditStatus = string.Empty;

    /// <summary>Leaving the Edit tab: a message that belonged to editing text must not stay in other modes.</summary>
    public void ClearTextEditStatus()
    {
        if (StatusMessage == Loc.T("Click on a line of text to edit it") ||
            (_textEditStatus.Length > 0 && StatusMessage == _textEditStatus))
        {
            StatusMessage = string.Empty;
        }

        _textEditStatus = string.Empty;
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

    /// <summary>Alignment belongs only to existing lines, and is a separate undoable edit.</summary>
    internal void ChangeTextEditAlignment(TextAlignment alignment)
    {
        if (TextStyleBar.Target is not TextEditViewModel edit || edit.Alignment == alignment)
        {
            return;
        }

        var before = edit.Alignment;
        SetAlignment(alignment);
        _undo.Push(() => SetAlignment(before), () => SetAlignment(alignment));

        void SetAlignment(TextAlignment value)
        {
            edit.Alignment = value;
            if (edit.IsChanged || edit.IsEditing)
            {
                if (!_textEdits.Contains(edit)) AttachTextEdit(edit);
            }
            else
            {
                DetachTextEdit(edit);
            }

            TextStyleBar.Refresh();
            OnEditsChanged();
        }
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
        var after = cancel ? before : text.TrimEnd();
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

        var replaced = changed.Where(e => !string.IsNullOrWhiteSpace(e.Text)).ToList();
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
        try
        {
            return PdfTextEditor.Apply(pdf, edits, _document?.OpenPassword);
        }
        catch (PdfException ex) when (System.Text.RegularExpressions.Regex.Match(ex.Message,
            @"^The text on page ([0-9, ]+) cannot be edited: it uses a font without exact measurements, or it overlaps an image\. Use a white box and Add text instead\.$") is { Success: true } match)
        {
            throw new PdfException(Loc.F("The text on page {0} cannot be edited: it uses a font without exact measurements, or it overlaps an image. Use a white box and Add text instead.", match.Groups[1].Value));
        }
    }

    /// <summary>The new text shaped and placed on the old line's baseline.</summary>
    internal static (int PageIndex, NormalizedPoint TopLeft, PdfColor Color, TextShaper.ShapedLine Line) Layout(TextEditViewModel edit, string family, string? previewText = null)
    {
        var text = (previewText ?? edit.Text).TrimEnd();
        var content = text.TrimStart();
        var leading = text[..(text.Length - content.Length)];
        var leadingWidth = leading.Length == 0 ? 0
            : TextShaper.Shape(leading, family, edit.IsBold, edit.IsItalic, edit.FontSize).Width;
        var shaped = TextShaper.Shape(content, family, edit.IsBold, edit.IsItalic, edit.FontSize);
        // Bounds from PDFium describe ink, not advance widths (which include side bearings).
        // Align that same visible edge, otherwise even a short Thai word can miss it by several points.
        var ink = TextEditInkBounds(shaped);
        var baseline = edit.Line.BaselineStart;
        var x = CalculateTextEditX(edit.AlignmentBounds, edit.AlignmentBaselineX, edit.Alignment,
            ink.Width / edit.Page.WidthPoints, leadingWidth / edit.Page.WidthPoints);
        if (edit.Alignment is TextAlignment.Center or TextAlignment.Right)
        {
            x -= ink.Left / edit.Page.WidthPoints;
        }
        var topLeft = new NormalizedPoint(x, baseline.Y - (shaped.Baseline / edit.Page.HeightPoints));
        return (edit.Page.Index, topLeft, new PdfColor(edit.Color.R, edit.Color.G, edit.Color.B), shaped);
    }

    internal static Rect TextEditInkBounds(TextShaper.ShapedLine shaped)
    {
        var ink = Rect.Empty;
        foreach (var run in shaped.Runs)
        {
            var pen = run.X;
            foreach (var glyph in run.Glyphs)
            {
                var box = run.Typeface.GetGlyphOutline(glyph.Id, run.FontSize, run.FontSize).Bounds;
                if (!box.IsEmpty)
                {
                    box.Offset(pen + glyph.OffsetX, run.Baseline - glyph.OffsetY);
                    ink.Union(box);
                }

                pen += glyph.Advance;
            }
        }

        return ink.IsEmpty ? new Rect(0, 0, shaped.Width, shaped.Height) : ink;
    }

    /// <summary>All distances are page-relative. Long text overflows in the chosen direction; no clamping.</summary>
    internal static double CalculateTextEditX(NormalizedRect bounds, double baselineX, TextAlignment alignment,
        double textWidth, double leadingWidth) => (alignment switch
        {
            TextAlignment.Center => bounds.X + ((bounds.Width - textWidth) / 2),
            TextAlignment.Right => bounds.X + bounds.Width - textWidth,
            _ => baselineX,
        }) + leadingWidth;

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
