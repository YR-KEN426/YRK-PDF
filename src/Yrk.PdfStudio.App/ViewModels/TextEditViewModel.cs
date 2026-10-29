using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// A line of existing text being edited. Until the document is saved it is shown as an overlay: a white
/// box over the old line with the new text on it. Its font, size, bold and italic start from the old line
/// and can be changed from the Edit toolbar.
/// </summary>
public sealed class TextEditViewModel : ObservableObject
{
    private const double Padding = 1; // points around the old line's box

    private readonly string _initialFamily;
    private readonly double _initialSize;
    private string _text;
    private bool _isEditing;
    private string _fontFamilyName;
    private double _fontSize;
    private bool _isBold;
    private bool _isItalic;
    private Color _color;

    /// <param name="fontSize">The size to start with: the line's, or one matched to a substitute font.</param>
    public TextEditViewModel(PageViewModel page, PdfTextLine line, string fontFamily, double fontSize)
    {
        Page = page;
        Line = line;
        _fontFamilyName = _initialFamily = fontFamily;
        _fontSize = _initialSize = fontSize;
        _isBold = line.IsBold;
        _isItalic = line.IsItalic;
        OriginalText = line.Text;
        _text = line.Text;
        _color = OriginalColor = Color.FromRgb(line.Color.R, line.Color.G, line.Color.B);
    }

    public PageViewModel Page { get; }

    public PdfTextLine Line { get; }

    public string OriginalText { get; }

    /// <summary>The installed font used for the new text (the document's font when it is installed).</summary>
    public string FontFamilyName
    {
        get => _fontFamilyName;
        set
        {
            if (SetProperty(ref _fontFamilyName, value))
            {
                OnPropertyChanged(nameof(FontFamily));
                OnPropertyChanged(nameof(IsChanged));
            }
        }
    }

    public FontFamily FontFamily => new(Imaging.BundledFonts.Locate(FontFamilyName));

    /// <summary>In points, as on the page.</summary>
    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (SetProperty(ref _fontSize, Math.Clamp(value, 4, 400)))
            {
                OnPropertyChanged(nameof(IsChanged));
            }
        }
    }

    public bool IsBold
    {
        get => _isBold;
        set
        {
            if (SetProperty(ref _isBold, value))
            {
                OnPropertyChanged(nameof(FontWeight));
                OnPropertyChanged(nameof(IsChanged));
            }
        }
    }

    public bool IsItalic
    {
        get => _isItalic;
        set
        {
            if (SetProperty(ref _isItalic, value))
            {
                OnPropertyChanged(nameof(FontStyle));
                OnPropertyChanged(nameof(IsChanged));
            }
        }
    }

    public FontWeight FontWeight => IsBold ? FontWeights.Bold : FontWeights.Normal;

    public FontStyle FontStyle => IsItalic ? FontStyles.Italic : FontStyles.Normal;

    /// <summary>The colour of the new text (the old line's to begin with).</summary>
    public Color Color
    {
        get => _color;
        set
        {
            if (SetProperty(ref _color, value))
            {
                OnPropertyChanged(nameof(Foreground));
                OnPropertyChanged(nameof(IsChanged));
            }
        }
    }

    public Color OriginalColor { get; }

    public SolidColorBrush Foreground => new(_color);

    /// <summary>The look as the font bar shows and changes it.</summary>
    public TextStyle Style
    {
        get => new(FontFamilyName, FontSize, IsBold, IsItalic, Color);
        set
        {
            FontFamilyName = value.FontFamilyName;
            FontSize = value.FontSize;
            IsBold = value.IsBold;
            IsItalic = value.IsItalic;
            Color = value.Color;
        }
    }

    public double Left => (Line.Bounds.X * Page.WidthPoints) - Padding;

    public double Top => (Line.Bounds.Y * Page.HeightPoints) - Padding;

    public double Width => (Line.Bounds.Width * Page.WidthPoints) + (2 * Padding);

    public double Height => (Line.Bounds.Height * Page.HeightPoints) + (2 * Padding);

    /// <summary>Changed text, or the same text in another font, size or style.</summary>
    public bool IsChanged => _text != OriginalText
        || _fontFamilyName != _initialFamily
        || Math.Abs(_fontSize - _initialSize) > 0.01
        || _isBold != Line.IsBold
        || _isItalic != Line.IsItalic
        || _color != OriginalColor;

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(IsChanged));
            }
        }
    }

    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    /// <summary>True when the point (page-relative) is on this line.</summary>
    public bool Contains(NormalizedPoint point) =>
        point.X >= Line.Bounds.X && point.X <= Line.Bounds.X + Line.Bounds.Width
        && point.Y >= Line.Bounds.Y && point.Y <= Line.Bounds.Y + Line.Bounds.Height;
}
