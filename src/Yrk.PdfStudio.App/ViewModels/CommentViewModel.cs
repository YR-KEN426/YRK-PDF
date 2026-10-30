using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

public enum AnnotationTool
{
    Select,
    Highlight,
    Underline,
    StrikeOut,
    Note,
    Pen,
    Rectangle,
    Ellipse,
    Arrow,
    Line,
    Redact,
    EditText,
    MeasureDistance,
    MeasurePerimeter,
    MeasureArea,
    Calibrate,
    Count,
    FieldText,
    FieldMultiline,
    FieldCheckBox,
    FieldDropdown,
    FieldSignature,
    FieldEdit,
}

/// <summary>One annotation in the comments panel. Its text can be edited in place.</summary>
public sealed class CommentViewModel : ObservableObject
{
    private string _editText;
    private bool _isSelected;

    public CommentViewModel(PdfAnnotation annotation)
    {
        Annotation = annotation;
        _editText = annotation.Contents;
        var color = annotation.Color is { } c ? Color.FromRgb(c.R, c.G, c.B) : DefaultColor(annotation.Kind);
        Brush = new SolidColorBrush(color);
        Brush.Freeze();
    }

    public PdfAnnotation Annotation { get; }

    public int PageIndex => Annotation.PageIndex;

    public PdfAnnotationRef Reference => new(Annotation.PageIndex, Annotation.Index, Annotation.Id);

    public string KindLabel => Loc.T(Annotation.Kind switch
    {
        PdfAnnotationKind.Note => "Note",
        PdfAnnotationKind.Highlight => "Highlight",
        PdfAnnotationKind.Underline => "Underline",
        PdfAnnotationKind.StrikeOut => "Strikeout",
        PdfAnnotationKind.Squiggly => "Squiggly",
        PdfAnnotationKind.Ink => "Drawing",
        PdfAnnotationKind.Square => "Rectangle",
        PdfAnnotationKind.Circle => "Ellipse",
        PdfAnnotationKind.Line => "Line",
        PdfAnnotationKind.FreeText => "Text box",
        PdfAnnotationKind.Stamp => "Stamp",
        PdfAnnotationKind.Polygon or PdfAnnotationKind.PolyLine => "Shape",
        PdfAnnotationKind.FileAttachment => "Attachment",
        PdfAnnotationKind.Caret => "Insert text",
        _ => "Annotation",
    });

    public string PageLabel => Loc.F("Page {0}", Annotation.PageIndex + 1);

    public string Meta
    {
        get
        {
            var date = ParsePdfDate(Annotation.Modified)?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            return string.Join(" · ", new[] { Annotation.Author, date }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    public Brush Brush { get; }

    /// <summary>The comment text as being edited; saved into the PDF when the editor loses focus.</summary>
    public string EditText
    {
        get => _editText;
        set => SetProperty(ref _editText, value ?? string.Empty);
    }

    public bool IsModified => _editText != Annotation.Contents;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Parses "D:YYYYMMDDHHmmSS+HH'mm'" (any trailing part may be missing).</summary>
    internal static DateTimeOffset? ParsePdfDate(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var s = value.StartsWith("D:", StringComparison.Ordinal) ? value[2..] : value;
        var digits = new string(s.TakeWhile(char.IsAsciiDigit).ToArray());
        if (digits.Length < 4)
        {
            return null;
        }

        int Part(int start, int length, int fallback) =>
            digits.Length >= start + length ? int.Parse(digits.AsSpan(start, length), CultureInfo.InvariantCulture) : fallback;

        var offset = TimeSpan.Zero;
        var rest = s[digits.Length..];
        if (rest.Length >= 3 && (rest[0] == '+' || rest[0] == '-')
            && int.TryParse(rest.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours))
        {
            var minutes = rest.Length >= 6 && int.TryParse(rest.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 0;
            offset = new TimeSpan(hours, minutes, 0) * (rest[0] == '-' ? -1 : 1);
        }

        try
        {
            return new DateTimeOffset(Part(0, 4, 1), Part(4, 2, 1), Part(6, 2, 1), Part(8, 2, 0), Part(10, 2, 0), Part(12, 2, 0), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static Color DefaultColor(PdfAnnotationKind kind) => kind switch
    {
        PdfAnnotationKind.Highlight or PdfAnnotationKind.Note => Color.FromRgb(0xFF, 0xD4, 0x3B),
        PdfAnnotationKind.Underline or PdfAnnotationKind.Squiggly => Color.FromRgb(0x2E, 0x7D, 0x32),
        PdfAnnotationKind.StrikeOut or PdfAnnotationKind.Ink or PdfAnnotationKind.Square or PdfAnnotationKind.Circle => Color.FromRgb(0xC6, 0x28, 0x28),
        _ => Color.FromRgb(0x75, 0x75, 0x75),
    };
}
