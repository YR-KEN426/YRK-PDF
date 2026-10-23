namespace Yrk.PdfStudio.Core.Pdf;

/// <summary>Page size in PDF points (1/72 inch) with the page's /Rotate already applied.</summary>
public readonly record struct PdfPageSize(double Width, double Height);

/// <summary>Opaque BGRA32 pixels (alpha byte unused) produced by rendering a page.</summary>
public sealed record PdfBitmap(int Width, int Height, int Stride, byte[] Pixels);

/// <summary>An outline (bookmark) entry. <see cref="PageIndex"/> is null when it has no in-document destination.</summary>
public sealed record PdfBookmark(string Title, int? PageIndex, IReadOnlyList<PdfBookmark> Children);

/// <summary>A rectangle relative to the displayed page: 0..1 on both axes, origin at the top-left corner.</summary>
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height);

/// <summary>One occurrence of a search query; a match that wraps lines has several rectangles.</summary>
public sealed record PdfTextMatch(int PageIndex, int CharIndex, int CharCount, IReadOnlyList<NormalizedRect> Rects);

[Flags]
public enum PdfSearchOptions
{
    None = 0,
    MatchCase = 1,
    WholeWord = 2,
}

/// <summary>Values match PDFium's FPDF_FORMFIELD_* constants.</summary>
public enum PdfFormFieldType
{
    Unknown = 0,
    PushButton = 1,
    CheckBox = 2,
    RadioButton = 3,
    ComboBox = 4,
    ListBox = 5,
    Text = 6,
    Signature = 7,
}

/// <summary>
/// One widget of an AcroForm field. <see cref="AnnotIndex"/> identifies the widget on its page;
/// several widgets can share a <see cref="Name"/> (radio groups).
/// </summary>
public sealed record PdfFormField(
    int PageIndex,
    int AnnotIndex,
    string Name,
    PdfFormFieldType Type,
    NormalizedRect Bounds,
    string Value,
    bool IsChecked,
    bool IsReadOnly,
    bool IsMultiline,
    bool IsPassword,
    bool IsEditableChoice,
    IReadOnlyList<string> Options,
    int SelectedIndex,
    double FontSize,
    bool IsRequired = false,
    bool IsComb = false,
    bool HasPicture = false);

/// <summary>A change to one widget. Only the members that apply to the field type are used.</summary>
public sealed record PdfFormFieldEdit(
    int PageIndex,
    int AnnotIndex,
    string? Text = null,
    bool? IsChecked = null,
    int? SelectedIndex = null);

/// <summary>BGRA32 pixels with straight (not premultiplied) alpha, rows packed (stride = Width × 4).</summary>
public sealed record PdfImage(int Width, int Height, byte[] Pixels);

// --- Text selection and annotations ----------------------------------------------------------------

/// <summary>A point relative to the displayed page: 0..1 on both axes, origin at the top-left corner.</summary>
public readonly record struct NormalizedPoint(double X, double Y);

/// <summary>Selected characters on one page, with their highlight rectangles.</summary>
public sealed record PdfTextSelection(int PageIndex, int StartIndex, int Count, IReadOnlyList<NormalizedRect> Rects, string Text);

public readonly record struct PdfColor(byte R, byte G, byte B, byte A = 255);

/// <summary>Annotation subtypes (values match PDFium's FPDF_ANNOT_* constants).</summary>
public enum PdfAnnotationKind
{
    Unknown = 0,
    Note = 1,
    Link = 2,
    FreeText = 3,
    Line = 4,
    Square = 5,
    Circle = 6,
    Polygon = 7,
    PolyLine = 8,
    Highlight = 9,
    Underline = 10,
    Squiggly = 11,
    StrikeOut = 12,
    Stamp = 13,
    Caret = 14,
    Ink = 15,
    Popup = 16,
    FileAttachment = 17,
    Widget = 20,
}

/// <param name="Index">Position in the page's annotation list when it was read.</param>
/// <param name="Id">The annotation name (/NM). Annotations made by this app always have one; others may not.</param>
/// <param name="Modified">The raw PDF date (/M), for example "D:20261017213000+07'00'".</param>
public sealed record PdfAnnotation(
    int PageIndex,
    int Index,
    string Id,
    PdfAnnotationKind Kind,
    NormalizedRect Bounds,
    PdfColor? Color,
    string Contents,
    string Author,
    string Modified)
{
    /// <summary>Comment-like annotations shown in the comments list (not links, form fields or popups).</summary>
    public bool IsComment => Kind is not (PdfAnnotationKind.Link or PdfAnnotationKind.Widget or PdfAnnotationKind.Popup or PdfAnnotationKind.Unknown);
}

/// <summary>Finds an annotation again: by /NM when it has one, otherwise by its index.</summary>
public readonly record struct PdfAnnotationRef(int PageIndex, int Index, string Id);

/// <summary>What to create. Common properties: colour, comment text and author.</summary>
public abstract record PdfAnnotationSpec(PdfColor Color, string? Contents, string? Author);

/// <summary>Highlight, underline, squiggly or strike-out over text.</summary>
public sealed record PdfTextMarkupSpec(PdfAnnotationKind Kind, IReadOnlyList<NormalizedRect> Rects, PdfColor Color, string? Contents = null, string? Author = null)
    : PdfAnnotationSpec(Color, Contents, Author);

/// <summary>A sticky note at a point.</summary>
public sealed record PdfNoteSpec(NormalizedPoint Position, PdfColor Color, string? Contents = null, string? Author = null)
    : PdfAnnotationSpec(Color, Contents, Author);

/// <summary>Freehand pen strokes; <paramref name="Width"/> in points.</summary>
public sealed record PdfInkSpec(IReadOnlyList<IReadOnlyList<NormalizedPoint>> Strokes, double Width, PdfColor Color, string? Contents = null, string? Author = null)
    : PdfAnnotationSpec(Color, Contents, Author);

/// <summary>Rectangle (Square) or ellipse (Circle) outline.</summary>
public sealed record PdfShapeSpec(PdfAnnotationKind Kind, NormalizedRect Bounds, double Width, PdfColor Color, string? Contents = null, string? Author = null)
    : PdfAnnotationSpec(Color, Contents, Author);

/// <summary>An arrow from <paramref name="Start"/> to <paramref name="End"/> (stored as ink, which every viewer draws).</summary>
public sealed record PdfArrowSpec(NormalizedPoint Start, NormalizedPoint End, double Width, PdfColor Color, string? Contents = null, string? Author = null)
    : PdfAnnotationSpec(Color, Contents, Author);

// --- Images to PDF ------------------------------------------------------------------------------

public enum PdfPageSizeMode
{
    /// <summary>A4, portrait or landscape to match each image.</summary>
    A4,

    /// <summary>US Letter, portrait or landscape to match each image.</summary>
    Letter,

    /// <summary>One page per image, sized to the image at <see cref="ImagesToPdfOptions.ImageDpi"/>.</summary>
    MatchImage,
}

/// <param name="Margin">Page margin in points (A4/Letter only). 28.35 pt = 10 mm.</param>
public sealed record ImagesToPdfOptions(PdfPageSizeMode PageSize = PdfPageSizeMode.A4, double Margin = 28.35, double ImageDpi = 150);

public abstract record PdfImageInput;

/// <summary>A JPEG file embedded as is (no quality loss, small files).</summary>
public sealed record PdfJpegInput(byte[] Data, int PixelWidth, int PixelHeight) : PdfImageInput;

/// <summary>Decoded pixels (PNG, screenshots…), stored losslessly.</summary>
public sealed record PdfBitmapInput(PdfImage Image) : PdfImageInput;

// --- Compression --------------------------------------------------------------------------------

/// <param name="MaxImageDpi">Images above this effective resolution are downsampled to it.</param>
/// <param name="RecompressLosslessImages">Also convert lossless (Flate) photos to JPEG when not downsampling.</param>
public sealed record PdfCompressionOptions(double MaxImageDpi = 150, int JpegQuality = 75, bool RecompressLosslessImages = false);

public sealed record PdfCompressionResult(long OriginalSize, long CompressedSize, int ImagesRecompressed)
{
    public double Reduction => OriginalSize == 0 ? 0 : 1 - ((double)CompressedSize / OriginalSize);
}

public enum PdfPixelFormat
{
    Gray8,
    Bgr24,
    Bgrx32,
    Bgra32,
}

public sealed record PdfRawPixels(int Width, int Height, int Stride, PdfPixelFormat Format, byte[] Pixels);

/// <summary>
/// JPEG decoding and encoding for compression. Supplied by the app (WPF/WIC) so Core stays free of
/// UI imaging libraries. Called on a background thread.
/// </summary>
public interface IPdfImageCodec
{
    /// <summary>Decodes a JPEG (grey or RGB); null if it cannot or should not be decoded (for example CMYK).</summary>
    PdfRawPixels? DecodeJpeg(byte[] data);

    /// <summary>Scales <paramref name="pixels"/> to the target size and encodes it as JPEG.</summary>
    byte[] EncodeJpeg(PdfRawPixels pixels, int targetWidth, int targetHeight, int quality);
}

/// <summary>Interactive work (pages on screen) always runs before background work (thumbnails, search).</summary>
public enum PdfWorkPriority
{
    Interactive,
    Background,
}

/// <summary>A line of existing text, as found under a click.</summary>
/// <param name="Bounds">The line's box on the page as displayed.</param>
/// <param name="BaselineStart">Where the first character sits on its baseline, on the page as displayed.</param>
/// <param name="FontSize">In points.</param>
/// <param name="FontName">The PDF font name without any subset prefix, for example "Sarabun-Bold".</param>
public sealed record PdfTextLine(
    int PageIndex,
    string Text,
    NormalizedRect Bounds,
    NormalizedPoint BaselineStart,
    double FontSize,
    PdfColor Color,
    string FontName,
    bool IsBold,
    bool IsItalic);

/// <summary>A rectangle of pixels within a page rendered at a given size (origin top-left).</summary>
public readonly record struct PdfPixelRegion(int X, int Y, int Width, int Height);
