using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>An area marked for redaction, drawn on its page in PDF points.</summary>
public sealed class RedactionMarkViewModel(PageViewModel page, NormalizedRect bounds)
{
    public PageViewModel Page { get; } = page;

    public NormalizedRect Bounds { get; } = bounds;

    public double Left => Bounds.X * Page.WidthPoints;

    public double Top => Bounds.Y * Page.HeightPoints;

    public double Width => Bounds.Width * Page.WidthPoints;

    public double Height => Bounds.Height * Page.HeightPoints;
}
