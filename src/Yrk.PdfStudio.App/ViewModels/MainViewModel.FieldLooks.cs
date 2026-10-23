using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Forms;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// How filled-in form fields look when saved: text in the font and size people choose (PDFium's own drawing
/// cannot shape Thai and clips tall letters), and pictures in image fields. The app draws these fields itself
/// when saving; see <see cref="PdfFieldAppearance"/>.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Fields the app draws itself on saving, by widget.</summary>
    private readonly Dictionary<(int PageIndex, int AnnotIndex), FormFieldViewModel> _fieldLooks = [];

    /// <summary>Pictures larger than this (in pixels, longest side) are scaled down before going into the PDF.</summary>
    private const int MaxFieldImageSide = 1600;

    private FormFieldViewModel? _selectedFormField;
    private IRelayCommand? _applyFieldStyleToAllCommand;
    private IAsyncRelayCommand<FormFieldViewModel>? _pickFieldImageCommand;
    private IRelayCommand<FormFieldViewModel>? _clearFieldImageCommand;

    /// <summary>Fonts offered for form fields: ones with Thai letters that are installed on this PC.</summary>
    public static IReadOnlyList<string> FormFonts { get; } = InstalledFonts(
        FormFieldViewModel.DefaultFontFamily, "TH Sarabun New", "TH SarabunPSK", "Tahoma", "Angsana New", "Cordia New", "Browallia New", "Segoe UI", "Arial");

    public static IReadOnlyList<string> FormFontSizes { get; } =
        [Loc.T("Auto"), "8", "9", "10", "11", "12", "14", "16", "18", "20"];

    /// <summary>The text field whose font the toolbar shows (the one last clicked).</summary>
    public FormFieldViewModel? SelectedFormField
    {
        get => _selectedFormField;
        private set
        {
            if (_selectedFormField == value)
            {
                return;
            }

            if (_selectedFormField is not null)
            {
                _selectedFormField.IsSelected = false;
            }

            _selectedFormField = value;
            if (value is not null)
            {
                value.IsSelected = true;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedFormField));
            UpdateTextStyleTarget();
        }
    }

    public bool HasSelectedFormField => _selectedFormField is not null;

    /// <summary>Called by the view when a text field gets the keyboard focus.</summary>
    public void SelectFormField(FormFieldViewModel field)
    {
        if (field.Type == PdfFormFieldType.Text)
        {
            SelectItem(null); // The font bar follows the field just clicked.
            SelectedFormField = field;
        }
    }

    /// <summary>Gives every text field the selected field's font, size, weight, slant and colour.</summary>
    public IRelayCommand ApplyFieldStyleToAllCommand => _applyFieldStyleToAllCommand ??= new RelayCommand(() =>
    {
        if (_selectedFormField is not { } source)
        {
            return;
        }

        foreach (var other in Pages.SelectMany(p => p.Fields).Where(f => f.Type == PdfFormFieldType.Text && f != source))
        {
            other.CopyStyle(source, notify: true);
        }

        _defaultFieldStyle = source;
        StatusMessage = Loc.T("All text fields now use this font and size.");
    });

    /// <summary>The field whose look fields on pages not shown yet take, after "apply to all fields".</summary>
    private FormFieldViewModel? _defaultFieldStyle;

    public IAsyncRelayCommand<FormFieldViewModel> PickFieldImageCommand => _pickFieldImageCommand ??= new AsyncRelayCommand<FormFieldViewModel>(PickFieldImageAsync);

    public IRelayCommand<FormFieldViewModel> ClearFieldImageCommand => _clearFieldImageCommand ??= new RelayCommand<FormFieldViewModel>(widget => widget?.RemovePicture());

    private async Task PickFieldImageAsync(FormFieldViewModel? field)
    {
        if (field is null || _dialogs.PickImageFile("Choose a picture for this field") is not { } path)
        {
            return;
        }

        try
        {
            var bitmap = await Task.Run(() => LoadFieldImage(path));
            field.SetImage(ImageConversion.ToPdfImage(bitmap), bitmap);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            _dialogs.ShowError(Loc.F("Couldn't open \"{0}\" as a picture.\n\n{1}", Path.GetFileName(path), ex.Message));
        }
    }

    /// <summary>Decodes a picture, upright and no larger than needed for a form field.</summary>
    private static BitmapSource LoadFieldImage(string path)
    {
        var image = new BitmapImage();
        using (var stream = File.OpenRead(path))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
        }

        BitmapSource result = image;
        var longest = Math.Max(image.PixelWidth, image.PixelHeight);
        if (longest > MaxFieldImageSide)
        {
            var scale = (double)MaxFieldImageSide / longest;
            result = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        }

        var converted = new FormatConvertedBitmap(result, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private void TrackFieldLook(FormFieldViewModel field)
    {
        if (field.HasLook)
        {
            _fieldLooks[field.Key] = field;
        }
        else
        {
            _fieldLooks.Remove(field.Key);
        }
    }

    /// <summary>A field shown again after a redraw keeps the font and picture chosen before.</summary>
    private void RestoreFieldLook(FormFieldViewModel field)
    {
        if (_fieldLooks.TryGetValue(field.Key, out var earlier))
        {
            field.RestoreLook(earlier);
            _fieldLooks[field.Key] = field;
        }
        else if (_defaultFieldStyle is { } style && field.Type == PdfFormFieldType.Text)
        {
            field.CopyStyle(style, notify: false);
        }
    }

    private void ClearFieldLooks()
    {
        _fieldLooks.Clear();
        _defaultFieldStyle = null;
        SelectedFormField = null;
    }

    /// <summary>
    /// The looks to write on saving: shaped text in each field's font (fonts that may not be embedded fall
    /// back to the default one) and chosen pictures. Empty for password-protected files, whose bytes the app
    /// cannot change: PDFium's own drawing is kept there.
    /// </summary>
    private List<PdfFieldLook> BuildFieldLooks(IPdfDocument document)
    {
        var looks = new List<PdfFieldLook>();
        if (_fieldLooks.Count == 0)
        {
            return looks;
        }

        try
        {
            if (PdfFiles.IsEncrypted(File.ReadAllBytes(document.FilePath)))
            {
                return looks;
            }
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException or FormatException)
        {
            return looks;
        }

        var texts = new List<(FormFieldViewModel Field, TextShaper.ShapedLine Line)>();
        foreach (var field in _fieldLooks.Values)
        {
            if (field.Image is { } image)
            {
                looks.Add(new PdfFieldLook(field.PageIndex, field.AnnotIndex, 0, Image: image));
            }
            else if (field.IsPictureRemoved)
            {
                looks.Add(new PdfFieldLook(field.PageIndex, field.AnnotIndex, 0, RemovePicture: true));
            }
            else if (field.Type == PdfFormFieldType.Text)
            {
                var wrap = field.IsMultiline ? Math.Max(1, field.TextWidth - 4) : 0;
                texts.Add((field, TextShaper.Shape(field.Text, field.FontFamilyName, field.IsBold, field.IsItalic, field.FontSize, wrap)));
            }
        }

        if (texts.Count > 0)
        {
            IReadOnlyList<PdfShapedText> shaped;
            try
            {
                shaped = TextShaper.ToShapedTexts(texts.Select(t => t.Line).ToList());
            }
            catch (InvalidOperationException)
            {
                // A chosen font does not allow embedding: use the default font for every field instead.
                shaped = TextShaper.ToShapedTexts(texts
                    .Select(t => TextShaper.Shape(t.Field.Text, FormFieldViewModel.DefaultFontFamily, bold: false, italic: false, t.Field.FontSize, t.Field.IsMultiline ? Math.Max(1, t.Field.TextWidth - 4) : 0))
                    .ToList());
            }

            for (var i = 0; i < texts.Count; i++)
            {
                var field = texts[i].Field;
                looks.Add(new PdfFieldLook(field.PageIndex, field.AnnotIndex, field.FontSize, shaped[i], Multiline: field.IsMultiline,
                    Color: field.TextColor == Colors.Black ? null : new PdfColor(field.TextColor.R, field.TextColor.G, field.TextColor.B)));
            }
        }

        return looks;
    }

    private static byte[] ApplyFieldLooks(byte[] pdf, IReadOnlyList<PdfFieldLook> looks) =>
        looks.Count == 0 ? pdf : PdfFieldAppearance.Apply(pdf, looks);

    private static IReadOnlyList<string> InstalledFonts(params string[] wanted)
    {
        var installed = new HashSet<string>(Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);
        return wanted.Where(name => name == FormFieldViewModel.DefaultFontFamily || installed.Contains(name)).ToList();
    }
}
