using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// The one font bar on the toolbar (font, size, bold, italic, colour). It works on whichever text is current:
/// the line being edited (Edit tab), the selected added text, the next text to add, or the form field last
/// clicked, so every kind of text is styled with the same controls and the same fonts.
/// </summary>
public sealed class TextStyleBarViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    internal TextStyleBarViewModel(MainViewModel owner) => _owner = owner;

    /// <summary>A form field, an added text item, a line being edited, or null for the next text to add.</summary>
    internal object? Target { get; private set; }

    public bool IsVisible { get; private set; }

    public string Label => Target switch
    {
        FormFieldViewModel => Loc.T("Form field:"),
        TextEditViewModel => Loc.T("Edited text:"),
        _ => Loc.T("Text:"),
    };

    public bool IsFormField => Target is FormFieldViewModel;

    /// <summary>Form fields can also fit the text to the box ("Auto").</summary>
    public IReadOnlyList<string> SizeChoices => Target is FormFieldViewModel ? FieldSizes : PlainSizes;

    public string FontFamilyName
    {
        get => Current.FontFamilyName;
        set
        {
            // Typing in the box: only a whole, installed font name is applied.
            if (TextStyles.Resolve(value) is { } family && family != Current.FontFamilyName)
            {
                _owner.ChangeTextStyle(s => s with { FontFamilyName = family });
            }
        }
    }

    public string FontSizeText
    {
        get => Target is FormFieldViewModel formField ? formField.FontSizeText : Current.FontSize.ToString("0.#", CultureInfo.CurrentCulture);
        set
        {
            if (Target is FormFieldViewModel formField)
            {
                formField.FontSizeText = value;
                Refresh();
            }
            else if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var size) && size >= 4 && size <= 400
                && Math.Abs(size - Current.FontSize) > 0.01)
            {
                _owner.ChangeTextStyle(s => s with { FontSize = size });
            }
        }
    }

    public bool IsBold
    {
        get => Current.IsBold;
        set => _owner.ChangeTextStyle(s => s with { IsBold = value });
    }

    public bool IsItalic
    {
        get => Current.IsItalic;
        set => _owner.ChangeTextStyle(s => s with { IsItalic = value });
    }

    public Color Color
    {
        get => Current.Color;
        set => _owner.ChangeTextStyle(s => s with { Color = value });
    }

    public Brush ColorBrush => new SolidColorBrush(Current.Color);

    private TextStyle Current => _owner.CurrentTextStyle;

    private static IReadOnlyList<string> PlainSizes { get; } =
        TextStyles.Sizes.Select(s => s.ToString(CultureInfo.CurrentCulture)).ToList();

    private static IReadOnlyList<string> FieldSizes { get; } = [Loc.T("Auto"), .. PlainSizes];

    internal void SetTarget(object? target, bool visible)
    {
        Target = target;
        IsVisible = visible;
        Refresh();
    }

    internal void Refresh() => OnPropertyChanged(string.Empty);
}
