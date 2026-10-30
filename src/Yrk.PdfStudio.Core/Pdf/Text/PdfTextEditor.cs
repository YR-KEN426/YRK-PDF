using Yrk.PdfStudio.Core.Pdf.Redaction;

namespace Yrk.PdfStudio.Core.Pdf.Text;

/// <summary>
/// A change to existing text: the characters inside <see cref="Area"/> are taken out of the page, and
/// <see cref="Replacement"/> (if any) is written in their place.
/// </summary>
public sealed record PdfTextEdit(int PageIndex, NormalizedRect Area, PdfTextBlock? Replacement);

public static class PdfTextEditor
{
    /// <summary>Applies text edits as incremental updates (earlier revisions stay in the file).</summary>
    /// <param name="password">The password of a protected file; the changes are encrypted like the rest of it.</param>
    /// <exception cref="PdfException">Text on a page uses a font whose characters cannot be measured, so it cannot be edited.</exception>
    public static byte[] Apply(byte[] pdf, IReadOnlyList<PdfTextEdit> edits, string? password = null)
    {
        if (edits.Count == 0)
        {
            return pdf;
        }

        var byPage = edits
            .GroupBy(e => e.PageIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<NormalizedRect>)g.Select(e => e.Area).ToList());
        var (removed, unsupported) = PdfRedactor.RemoveContent(pdf, byPage, blackBoxes: false, removeAnnotations: false, password: password);
        if (unsupported.Count > 0)
        {
            var pages = string.Join(", ", unsupported.Order().Select(p => p + 1));
            throw new PdfException($"The text on page {pages} cannot be edited: it uses a font without exact measurements, or it overlaps an image. Use a white box and Add text instead.");
        }

        var blocks = edits.Select(e => e.Replacement).OfType<PdfTextBlock>().Where(b => b.Runs.Count > 0).ToList();
        return blocks.Count == 0 ? removed : PdfTextWriter.AddText(removed, blocks, password);
    }
}
