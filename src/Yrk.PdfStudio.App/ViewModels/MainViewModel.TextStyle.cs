using Yrk.PdfStudio.App.Imaging;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>The font bar: which text it styles, and applying its changes (see <see cref="TextStyleBarViewModel"/>).</summary>
public sealed partial class MainViewModel
{
    private TextStyleBarViewModel? _textStyleBar;

    /// <summary>The look of the next text added; the last one chosen.</summary>
    private TextStyle _newTextStyle = TextStyle.Default;

    public TextStyleBarViewModel TextStyleBar => _textStyleBar ??= new TextStyleBarViewModel(this);

    /// <summary>
    /// Points the font bar at the current text: the line being edited on the Edit tab, else the selected added
    /// text, else the text about to be added, else the form field last clicked. Hidden when there is none.
    /// </summary>
    private void UpdateTextStyleTarget()
    {
        if (CurrentTool == AnnotationTool.EditText)
        {
            TextStyleBar.SetTarget(_activeTextEdit, _activeTextEdit is not null);
        }
        else if (_selectedItem is { IsText: true } item)
        {
            TextStyleBar.SetTarget(item, true);
        }
        else if (_placement is { Kind: PlacedItemKind.Text })
        {
            TextStyleBar.SetTarget(null, true);
        }
        else
        {
            TextStyleBar.SetTarget(_selectedFormField, _selectedFormField is not null);
        }
    }

    internal TextStyle CurrentTextStyle => TextStyleBar.Target switch
    {
        FormFieldViewModel formField => formField.Style,
        PlacedItemViewModel item => item.Style,
        TextEditViewModel edit => edit.Style,
        _ => _newTextStyle,
    };

    /// <summary>Applies a font bar change to the current text; added text also remembers it for the next one.</summary>
    internal void ChangeTextStyle(Func<TextStyle, TextStyle> change)
    {
        switch (TextStyleBar.Target)
        {
            case FormFieldViewModel field:
                field.Style = change(field.Style);
                break;
            case PlacedItemViewModel item:
                SetPlacedTextStyle(item, change(item.Style));
                _newTextStyle = item.Style;
                break;
            case TextEditViewModel edit:
                edit.Style = change(edit.Style);
                StyleChanged(edit);
                break;
            default:
                _newTextStyle = change(_newTextStyle);
                break;
        }

        TextStyleBar.Refresh();
    }

    /// <summary>Restyles added text: its box grows or shrinks to fit, and the change can be undone.</summary>
    private void SetPlacedTextStyle(PlacedItemViewModel item, TextStyle style)
    {
        var (oldStyle, oldBounds) = (item.Style, item.Bounds);
        if (oldStyle == style)
        {
            return;
        }

        item.Style = style;
        if (item.Text.Length > 0)
        {
            var size = TextRendering.Measure(item.Text, style);
            item.Bounds = item.ToBounds(item.Left, item.Top, size.Width, size.Height);
        }

        var newBounds = item.Bounds;
        _undo.Push(
            () => { item.Style = oldStyle; item.Bounds = oldBounds; TextStyleBar.Refresh(); },
            () => { item.Style = style; item.Bounds = newBounds; TextStyleBar.Refresh(); });
        OnEditsChanged();
    }
}
