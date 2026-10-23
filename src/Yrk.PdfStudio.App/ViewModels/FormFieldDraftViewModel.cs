using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Forms;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>A new form field placed on a page, drawn in PDF points until the document is saved.</summary>
public sealed class FormFieldDraftViewModel(PageViewModel page, PdfNewField field)
{
    public PageViewModel Page { get; } = page;

    public PdfNewField Field { get; } = field;

    public double Left => Field.Bounds.X * Page.WidthPoints;

    public double Top => Field.Bounds.Y * Page.HeightPoints;

    public double Width => Field.Bounds.Width * Page.WidthPoints;

    public double Height => Field.Bounds.Height * Page.HeightPoints;

    /// <summary>What the box shows: its name, with a star when it must be filled in.</summary>
    public string Label => Field.Name + (Field.Required ? " *" : string.Empty);

    /// <summary>Check boxes are too small for a label inside.</summary>
    public bool ShowLabel => Field.Kind != PdfNewFieldKind.CheckBox;
}
