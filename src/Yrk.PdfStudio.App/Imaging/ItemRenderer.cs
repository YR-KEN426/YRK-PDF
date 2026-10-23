using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>Produces the image that a placed item becomes when it is written into the PDF.</summary>
internal static class ItemRenderer
{
    public static PdfImage Render(PlacedItemViewModel item) =>
        ImageConversion.ToPdfImage(item.Kind == PlacedItemKind.Text
            ? TextRendering.Render(item.Text, item.Style)
            : item.Image!);
}
