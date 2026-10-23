using System.Windows;
using System.Windows.Controls;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Controls;

/// <summary>Picks the editor for a form widget by its field type.</summary>
public sealed class FormFieldTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }

    public DataTemplate? CheckBoxTemplate { get; set; }

    public DataTemplate? RadioButtonTemplate { get; set; }

    public DataTemplate? ChoiceTemplate { get; set; }

    public DataTemplate? ImageTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is FormFieldViewModel field
            ? field.Type switch
            {
                PdfFormFieldType.Text => TextTemplate,
                PdfFormFieldType.CheckBox => CheckBoxTemplate,
                PdfFormFieldType.RadioButton => RadioButtonTemplate,
                PdfFormFieldType.ComboBox or PdfFormFieldType.ListBox => ChoiceTemplate,
                PdfFormFieldType.PushButton when field.IsImageField => ImageTemplate,
                _ => null,
            }
            : null;
}
