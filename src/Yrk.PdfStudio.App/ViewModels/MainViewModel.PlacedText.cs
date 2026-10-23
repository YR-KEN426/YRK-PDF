using System.IO;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// Text added with Fill &amp; Sign and the words of a สำเนาถูกต้อง stamp are written as real text (searchable,
/// copyable, sharp), shaped like the on-screen preview and embedded as a font subset. Password-protected files keep
/// the earlier image form.
/// </summary>
public sealed partial class MainViewModel
{
    private bool HasPlacedText => _placedItems.Any(i => (i.IsText && i.Text.Trim().Length > 0) || i.HasStampText);

    /// <summary>Real text needs the file's own bytes, which an encrypted file does not allow us to change.</summary>
    private bool CanWriteRealText(IPdfDocument document)
    {
        if (!HasPlacedText)
        {
            return false;
        }

        try
        {
            return !PdfFiles.IsEncrypted(File.ReadAllBytes(document.FilePath));
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Writes every added text item into the PDF bytes, one shaped line at a time.</summary>
    private byte[] ApplyPlacedText(byte[] pdf)
    {
        try
        {
            return ApplyPlacedText(pdf, fallback: false);
        }
        catch (InvalidOperationException)
        {
            // A chosen font does not allow embedding: write all added text in the default font instead.
            return ApplyPlacedText(pdf, fallback: true);
        }
    }

    private byte[] ApplyPlacedText(byte[] pdf, bool fallback)
    {
        var lines = new List<(int PageIndex, NormalizedPoint TopLeft, PdfColor Color, TextShaper.ShapedLine Line)>();
        foreach (var item in _placedItems.Where(i => i.IsText && i.Text.Trim().Length > 0))
        {
            var style = item.Style;
            var color = new PdfColor(style.Color.R, style.Color.G, style.Color.B);
            var y = TextRendering.Padding;
            foreach (var text in item.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var shaped = TextShaper.Shape(text.Length == 0 ? " " : text, fallback ? TextShaper.ThaiFallbackFamily : style.FontFamilyName, style.IsBold, style.IsItalic, style.FontSize);
                if (text.Trim().Length > 0)
                {
                    var topLeft = new NormalizedPoint(
                        item.Bounds.X + (TextRendering.Padding / item.PageWidth),
                        item.Bounds.Y + (y / item.PageHeight));
                    lines.Add((item.PageIndex, topLeft, color, shaped));
                }

                y += shaped.Height;
            }
        }

        // A stamp's words, centred in its frame as drawn on screen, at the size the stamp was given.
        foreach (var item in _placedItems.Where(i => i.HasStampText))
        {
            var stamp = item.Stamp!;
            var scale = item.Width / stamp.Size.Width;
            var color = new PdfColor(stamp.Ink.R, stamp.Ink.G, stamp.Ink.B);
            foreach (var line in stamp.Lines!)
            {
                var shaped = TextShaper.Shape(line.Text, TextShaper.ThaiFallbackFamily, line.Bold, italic: false, line.FontSize * scale);
                var topLeft = new NormalizedPoint(
                    item.Bounds.X + (line.TopLeft.X * scale / item.PageWidth),
                    item.Bounds.Y + (line.TopLeft.Y * scale / item.PageHeight));
                lines.Add((item.PageIndex, topLeft, color, shaped));
            }
        }

        return lines.Count == 0 ? pdf : PdfTextWriter.AddText(pdf, TextShaper.ToBlocks(lines));
    }
}
