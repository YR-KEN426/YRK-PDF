using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// An editable form widget shown over the page. Positions and sizes are in PDF points: the overlay
/// lives in a canvas the size of the page in points, which a Viewbox scales to the current zoom.
/// </summary>
public sealed class FormFieldViewModel : ObservableObject
{
    /// <summary>The font fields are typed and saved in unless the user picks another.</summary>
    public const string DefaultFontFamily = TextStyles.DefaultFamily;

    private readonly Action<FormFieldViewModel> _changed;
    private string _text;
    private bool _isChecked;
    private int _selectedIndex;
    private string _fontFamilyName = DefaultFontFamily;
    private double _fontSizeChoice;
    private bool _isBold;
    private bool _isItalic;
    private Color _color = Colors.Black;
    private bool _isSelected;
    private PdfImage? _image;
    private ImageSource? _imagePreview;
    private bool _pictureRemoved;

    /// <param name="textRotation">How far the field's text is turned on the shown page, in degrees anticlockwise.</param>
    public FormFieldViewModel(PdfFormField field, double pageWidth, double pageHeight, Action<FormFieldViewModel> changed, int textRotation = 0)
    {
        Field = field;
        _changed = changed;
        _text = field.Value;
        _isChecked = field.IsChecked;
        _selectedIndex = field.SelectedIndex;

        Left = field.Bounds.X * pageWidth;
        Top = field.Bounds.Y * pageHeight;
        Width = Math.Max(4, field.Bounds.Width * pageWidth);
        Height = Math.Max(4, field.Bounds.Height * pageHeight);
        TextRotation = textRotation;
    }

    /// <summary>How far the text is turned on the shown page, in degrees anticlockwise (0, 90, 180 or 270).</summary>
    public int TextRotation { get; }

    /// <summary>The same turn for WPF, which turns clockwise.</summary>
    public double TextAngle => -TextRotation;

    private bool IsSideways => TextRotation is 90 or 270;

    /// <summary>The box along its text: how long a line can be.</summary>
    public double TextWidth => IsSideways ? Height : Width;

    /// <summary>The box across its text: how tall a line can be.</summary>
    public double TextHeight => IsSideways ? Width : Height;

    public PdfFormField Field { get; }

    public PdfFormFieldType Type => Field.Type;

    public int PageIndex => Field.PageIndex;

    public int AnnotIndex => Field.AnnotIndex;

    /// <summary>Radio buttons with the same field name form one group.</summary>
    public string GroupName => $"pdf-field:{Field.Name}";

    public IReadOnlyList<string> Options => Field.Options;

    public bool IsMultiline => Field.IsMultiline;

    /// <summary>
    /// A picture box: a push button made with Acrobat's image field tool (named "…_af_image"), or one whose
    /// name says it holds a picture. Clicking it lets people choose an image, as in Acrobat.
    /// </summary>
    public bool IsImageField => Type == PdfFormFieldType.PushButton && IsImageName(Field.Name);

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }

    /// <summary>The font text is shown and saved in.</summary>
    public string FontFamilyName
    {
        get => _fontFamilyName;
        set
        {
            if (SetProperty(ref _fontFamilyName, string.IsNullOrWhiteSpace(value) ? DefaultFontFamily : value))
            {
                OnPropertyChanged(nameof(FontSize));
                OnPropertyChanged(nameof(FontSizeText));
                OnPropertyChanged(nameof(IsEdited));
                _changed(this);
            }
        }
    }

    /// <summary>The size picked by the user in points, or 0 for "fit the box".</summary>
    public double FontSizeChoice
    {
        get => _fontSizeChoice;
        set
        {
            if (SetProperty(ref _fontSizeChoice, Math.Clamp(value, 0, 72)))
            {
                OnPropertyChanged(nameof(FontSize));
                OnPropertyChanged(nameof(FontSizeText));
                OnPropertyChanged(nameof(IsEdited));
                _changed(this);
            }
        }
    }

    /// <summary>The size combo box's text: "Auto" or a number.</summary>
    public string FontSizeText
    {
        get => _fontSizeChoice > 0 ? _fontSizeChoice.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) : Loc.T("Auto");
        set
        {
            // Applied while typing: a partial number ("1" on the way to "12") is ignored rather than reset.
            if (string.IsNullOrWhiteSpace(value) || value == Loc.T("Auto"))
            {
                FontSizeChoice = 0;
            }
            else if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var size) && size >= 4)
            {
                FontSizeChoice = size;
            }
        }
    }

    /// <summary>
    /// The size text is shown and saved at. "Auto" keeps the form's own size when it fits, and otherwise
    /// shrinks it until a line of this font, Thai marks above and below included, fits the box.
    /// </summary>
    public double FontSize => _fontSizeChoice > 0 ? _fontSizeChoice : AutoFontSize();

    private double AutoFontSize()
    {
        var formSize = Field.FontSize > 0 ? Field.FontSize : 12;
        if (IsMultiline)
        {
            return Math.Min(formSize, 10);
        }

        var lineSpacing = new FontFamily(_fontFamilyName + ", " + DefaultFontFamily).LineSpacing;
        var fitting = (TextHeight - 2) / Math.Max(1, lineSpacing);
        return Math.Round(Math.Clamp(Math.Min(formSize, fitting), 4, formSize), 1);
    }

    /// <summary>The look as the font bar shows and changes it (the size there goes through <see cref="FontSizeText"/>).</summary>
    public TextStyle Style
    {
        get => new(_fontFamilyName, FontSize, _isBold, _isItalic, _color);
        set
        {
            FontFamilyName = value.FontFamilyName;
            if (Math.Abs(value.FontSize - FontSize) > 0.01)
            {
                FontSizeChoice = value.FontSize;
            }

            if ((_isBold, _isItalic, _color) != (value.IsBold, value.IsItalic, value.Color))
            {
                (_isBold, _isItalic, _color) = (value.IsBold, value.IsItalic, value.Color);
                OnPropertyChanged(nameof(FontWeight));
                OnPropertyChanged(nameof(FontStyle));
                OnPropertyChanged(nameof(Foreground));
                OnPropertyChanged(nameof(IsEdited));
                _changed(this);
            }
        }
    }

    public bool IsBold => _isBold;

    public bool IsItalic => _isItalic;

    public Color TextColor => _color;

    public System.Windows.FontWeight FontWeight => _isBold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;

    public System.Windows.FontStyle FontStyle => _isItalic ? System.Windows.FontStyles.Italic : System.Windows.FontStyles.Normal;

    public Brush Foreground => new SolidColorBrush(_color);

    /// <summary>A font, size, weight, slant or colour other than the default was chosen.</summary>
    private bool IsStyled => _fontSizeChoice > 0 || _fontFamilyName != DefaultFontFamily || _isBold || _isItalic || _color != Colors.Black;

    /// <summary>The field whose font the toolbar shows; drawn with an outline.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>The picture chosen for an image field, to save into the PDF.</summary>
    public PdfImage? Image => _image;

    public ImageSource? ImagePreview => _imagePreview;

    public bool HasImage => _image is not null;

    /// <summary>The picture saved in the file earlier is to be taken out on saving.</summary>
    public bool IsPictureRemoved => _pictureRemoved;

    /// <summary>The field shows a picture: one chosen now, or one saved earlier and not removed.</summary>
    public bool ShowsPicture => _image is not null || (Field.HasPicture && !_pictureRemoved);

    /// <summary>A white box hides the saved picture on the page, which stays drawn until the file is saved.</summary>
    public bool CoversSavedPicture => Field.HasPicture && (_pictureRemoved || _image is not null);

    public void SetImage(PdfImage? image, ImageSource? preview)
    {
        _image = image;
        _imagePreview = preview;
        _pictureRemoved = false;
        NotifyPicture();
        _changed(this);
    }

    /// <summary>Takes the picture out: the one chosen now, or else the one saved in the file.</summary>
    public void RemovePicture()
    {
        if (_image is null && !Field.HasPicture)
        {
            return;
        }

        // Removing a picture chosen over a saved one also removes the saved one: what was shown is gone.
        _pictureRemoved = Field.HasPicture;
        _image = null;
        _imagePreview = null;
        NotifyPicture();
        _changed(this);
    }

    private void NotifyPicture()
    {
        OnPropertyChanged(nameof(ImagePreview));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(IsPictureRemoved));
        OnPropertyChanged(nameof(ShowsPicture));
        OnPropertyChanged(nameof(CoversSavedPicture));
    }

    /// <summary>
    /// True when the app draws this field itself on saving: text that was typed or restyled (shaped Thai in
    /// the chosen font and size), a chosen picture, or a saved picture to take out.
    /// </summary>
    /// <remarks>Password fields (shown as dots) and comb fields (one character per box) are left to PDFium's drawing.</remarks>
    public bool HasLook => IsImageField
        ? _image is not null || _pictureRemoved
        : Type == PdfFormFieldType.Text && !Field.IsPassword && !Field.IsComb && _text.Trim().Length > 0
            && (_text != Field.Value || IsStyled);

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsEdited));
                _changed(this);
            }
        }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _changed(this);
            }
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (SetProperty(ref _selectedIndex, value))
            {
                _changed(this);
            }
        }
    }

    public (int PageIndex, int AnnotIndex) Key => (Field.PageIndex, Field.AnnotIndex);

    /// <summary>The edit to write into the PDF, or null when the value equals the original.</summary>
    public PdfFormFieldEdit? ToEdit() => Type switch
    {
        PdfFormFieldType.Text when _text != Field.Value =>
            new PdfFormFieldEdit(PageIndex, AnnotIndex, Text: _text),
        PdfFormFieldType.CheckBox when _isChecked != Field.IsChecked =>
            new PdfFormFieldEdit(PageIndex, AnnotIndex, IsChecked: _isChecked),

        // Only the newly selected radio button is written; the form engine clears the rest of its group.
        PdfFormFieldType.RadioButton when _isChecked && !Field.IsChecked =>
            new PdfFormFieldEdit(PageIndex, AnnotIndex, IsChecked: true),
        PdfFormFieldType.ComboBox or PdfFormFieldType.ListBox when _selectedIndex != Field.SelectedIndex && _selectedIndex >= 0 =>
            new PdfFormFieldEdit(PageIndex, AnnotIndex, SelectedIndex: _selectedIndex),
        _ => null,
    };

    /// <summary>Restores an unsaved value after the document was reloaded.</summary>
    internal void Restore(PdfFormFieldEdit edit)
    {
        if (edit.Text is not null)
        {
            _text = edit.Text;
            OnPropertyChanged(nameof(Text));
        }

        if (edit.IsChecked is bool isChecked)
        {
            _isChecked = isChecked;
            OnPropertyChanged(nameof(IsChecked));
        }

        if (edit.SelectedIndex is int index)
        {
            _selectedIndex = index;
            OnPropertyChanged(nameof(SelectedIndex));
        }
    }

    /// <summary>Takes over the font and picture of the same field from before the page was redrawn.</summary>
    internal void RestoreLook(FormFieldViewModel earlier)
    {
        _fontFamilyName = earlier._fontFamilyName;
        _fontSizeChoice = earlier._fontSizeChoice;
        (_isBold, _isItalic, _color) = (earlier._isBold, earlier._isItalic, earlier._color);
        _image = earlier._image;
        _imagePreview = earlier._imagePreview;
        _pictureRemoved = earlier._pictureRemoved;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Takes the font, size, weight, slant and colour of another field ("apply to all fields").</summary>
    /// <param name="notify">False for a field shown after a redraw, which is not a change by the user.</param>
    internal void CopyStyle(FormFieldViewModel source, bool notify)
    {
        _fontFamilyName = source._fontFamilyName;
        _fontSizeChoice = source._fontSizeChoice;
        (_isBold, _isItalic, _color) = (source._isBold, source._isItalic, source._color);
        OnPropertyChanged(string.Empty);
        if (notify)
        {
            _changed(this);
        }
    }

    /// <summary>
    /// The value or font changed since the file was opened. Until then the field's own saved appearance shows
    /// through the editor, so a reopened file looks exactly as it was saved.
    /// </summary>
    public bool IsEdited => _text != Field.Value || IsStyled;

    private static bool IsImageName(string name) =>
        name.EndsWith("_af_image", StringComparison.OrdinalIgnoreCase)
        || name.Contains("image", StringComparison.OrdinalIgnoreCase)
        || name.Contains("photo", StringComparison.OrdinalIgnoreCase)
        || name.Contains("picture", StringComparison.OrdinalIgnoreCase)
        || name.Contains("logo", StringComparison.OrdinalIgnoreCase)
        || name.Contains("รูป", StringComparison.Ordinal);
}
