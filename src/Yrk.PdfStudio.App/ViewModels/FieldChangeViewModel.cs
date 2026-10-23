using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Forms;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>A change waiting for Save on a field already in the document (red when it will be deleted).</summary>
public sealed class FieldChangeViewModel(PageViewModel page, NormalizedRect bounds, PdfFieldChange change)
{
    public PageViewModel Page { get; } = page;

    public NormalizedRect Bounds { get; } = bounds;

    public PdfFieldChange Change { get; set; } = change;

    public double Left => Bounds.X * Page.WidthPoints;

    public double Top => Bounds.Y * Page.HeightPoints;

    public double Width => Bounds.Width * Page.WidthPoints;

    public double Height => Bounds.Height * Page.HeightPoints;

    public bool IsDelete => Change.Delete;

    public string Label => Change.Delete
        ? Loc.F("Will be deleted: {0}", Change.FullName)
        : Loc.F("Will be changed: {0}", Change.NewName ?? Change.FullName);
}
