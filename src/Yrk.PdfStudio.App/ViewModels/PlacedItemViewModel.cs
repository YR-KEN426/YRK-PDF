using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

public enum PlacedItemKind
{
    Signature,
    Stamp,
    Text,
}

/// <summary>
/// Something the user added to a page (signature, stamp or text). It stays an overlay that can be
/// moved, resized and undone until the document is saved, when it is drawn into the page.
/// Geometry is in PDF points of the displayed page; <see cref="Bounds"/> is the page-relative form.
/// </summary>
public sealed class PlacedItemViewModel : ObservableObject
{
    public const double MinimumSize = 8;

    private NormalizedRect _bounds;
    private string _text;
    private bool _isSelected;
    private bool _isEditing;
    private TextStyle _style;

    private PlacedItemViewModel(int pageIndex, PlacedItemKind kind, double pageWidth, double pageHeight, BitmapSource? image, string text, TextStyle style)
    {
        PageIndex = pageIndex;
        Kind = kind;
        PageWidth = pageWidth;
        PageHeight = pageHeight;
        Image = image;
        _text = text;
        _style = style;
    }

    public int PageIndex { get; }

    public PlacedItemKind Kind { get; }

    public bool IsText => Kind == PlacedItemKind.Text;

    public bool IsImage => Kind != PlacedItemKind.Text;

    public bool ShowTextPreview => IsText && !_isEditing;

    public bool ShowResizeHandle => IsImage && _isSelected;

    public double PageWidth { get; }

    public double PageHeight { get; }

    /// <summary>Signature or stamp artwork as shown on screen (straight-alpha BGRA). Null for text.</summary>
    public BitmapSource? Image { get; }

    /// <summary>Text items: font, size (points), bold, italic and colour, chosen on the font bar.</summary>
    public TextStyle Style
    {
        get => _style;
        set
        {
            if (SetProperty(ref _style, value))
            {
                OnPropertyChanged(nameof(FontSize));
                OnPropertyChanged(nameof(FontFamily));
                OnPropertyChanged(nameof(FontWeight));
                OnPropertyChanged(nameof(FontStyle));
                OnPropertyChanged(nameof(Foreground));
            }
        }
    }

    public double FontSize => _style.FontSize;

    public FontFamily FontFamily => _style.FontFamily;

    public System.Windows.FontWeight FontWeight => _style.Weight;

    public System.Windows.FontStyle FontStyle => _style.Style;

    public Brush Foreground => new SolidColorBrush(_style.Color);

    public NormalizedRect Bounds
    {
        get => _bounds;
        set
        {
            if (SetProperty(ref _bounds, value))
            {
                OnPropertyChanged(nameof(Left));
                OnPropertyChanged(nameof(Top));
                OnPropertyChanged(nameof(Width));
                OnPropertyChanged(nameof(Height));
            }
        }
    }

    public double Left => _bounds.X * PageWidth;

    public double Top => _bounds.Y * PageHeight;

    public double Width => _bounds.Width * PageWidth;

    public double Height => _bounds.Height * PageHeight;

    /// <summary>Width / height of the artwork, kept when resizing images.</summary>
    public double AspectRatio => Art?.AspectRatio ?? (Image is null ? 0 : (double)Image.PixelWidth / Image.PixelHeight);

    /// <summary>A signature's artwork for the PDF; null for stamps and text.</summary>
    public Services.SignatureArt? Art { get; private init; }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value ?? string.Empty);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(ShowResizeHandle));
            }
        }
    }

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (SetProperty(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(ShowTextPreview));
            }
        }
    }

    /// <param name="art">For signatures: what is written into the PDF (vectors, a PDF page or the image).</param>
    public static PlacedItemViewModel CreateImage(int pageIndex, PlacedItemKind kind, double pageWidth, double pageHeight, BitmapSource image, Services.SignatureArt? art = null) =>
        new(pageIndex, kind, pageWidth, pageHeight, image, string.Empty, TextStyle.Default) { Art = art };

    public static PlacedItemViewModel CreateText(int pageIndex, double pageWidth, double pageHeight, TextStyle style) =>
        new(pageIndex, PlacedItemKind.Text, pageWidth, pageHeight, null, string.Empty, style);

    /// <summary>Converts a rectangle in page points to page-relative bounds, kept inside the page.</summary>
    public NormalizedRect ToBounds(double left, double top, double width, double height)
    {
        width = Math.Clamp(width, MinimumSize, PageWidth);
        height = Math.Clamp(height, MinimumSize, PageHeight);
        left = Math.Clamp(left, 0, PageWidth - width);
        top = Math.Clamp(top, 0, PageHeight - height);
        return new NormalizedRect(left / PageWidth, top / PageHeight, width / PageWidth, height / PageHeight);
    }
}
