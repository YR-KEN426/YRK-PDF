using System.IO;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// Text added with Fill &amp; Sign and the words of a สำเนาถูกต้อง stamp are written as real text (searchable,
/// copyable, sharp), shaped like the on-screen preview and embedded as a font subset.
/// </summary>
public sealed partial class MainViewModel
{
    private bool HasPlacedText => _placedItems.Any(i => (i.IsText && i.Text.Trim().Length > 0) || i.HasStampText);

    private bool CanWriteRealText => HasPlacedText;

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
        var owners = new List<PlacedItemViewModel>();
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
                    owners.Add(item);
                }

                y += shaped.Height;
            }
        }

        // A stamp's words, centred in its frame as drawn on screen, at the size the stamp was given.
        foreach (var item in _placedItems.Where(i => i.HasStampText))
        {
            AddStampWords(lines, item.Stamp!, item.PageIndex, item.Bounds, item.PageWidth, item.PageHeight);
            while (owners.Count < lines.Count) owners.Add(item);
        }

        if (lines.Count == 0) return pdf;
        var blocks = TextShaper.ToBlocks(lines).Select((block, index) =>
        {
            var item = owners[index];
            block = block with { Mark = new YrkMark(item.HasStampText ? YrkMarkKind.StampText : YrkMarkKind.Text, item.MarkGroup) };
            if (item.Rotation == 0 && item.Opacity == 1) return block;
            var angle = item.Rotation * Math.PI / 180;
            var centreX = item.Left + item.Width / 2;
            var centreY = item.Top + item.Height / 2;
            var dx = block.TopLeft.X * item.PageWidth - centreX;
            var dy = block.TopLeft.Y * item.PageHeight - centreY;
            var topLeft = new NormalizedPoint(
                (centreX + Math.Cos(angle) * dx - Math.Sin(angle) * dy) / item.PageWidth,
                (centreY + Math.Sin(angle) * dx + Math.Cos(angle) * dy) / item.PageHeight);
            // PdfTextWriter's positive angle is counter-clockwise; overlays use clockwise.
            return block with { TopLeft = topLeft, Rotation = -item.Rotation, Opacity = item.Opacity };
        }).ToList();
        return PdfTextWriter.AddText(pdf, blocks, _document?.OpenPassword);
    }

    /// <summary>The words of a stamp placed at <paramref name="bounds"/>, as lines of real text.</summary>
    private static void AddStampWords(
        List<(int PageIndex, NormalizedPoint TopLeft, PdfColor Color, TextShaper.ShapedLine Line)> lines,
        RenderedStamp stamp, int pageIndex, NormalizedRect bounds, double pageWidth, double pageHeight)
    {
        var scale = bounds.Width * pageWidth / stamp.Size.Width;
        var color = new PdfColor(stamp.Ink.R, stamp.Ink.G, stamp.Ink.B);
        foreach (var line in stamp.Lines!)
        {
            var shaped = TextShaper.Shape(line.Text, TextShaper.ThaiFallbackFamily, line.Bold, italic: false, line.FontSize * scale);
            var topLeft = new NormalizedPoint(
                bounds.X + (line.TopLeft.X * scale / pageWidth),
                bounds.Y + (line.TopLeft.Y * scale / pageHeight));
            lines.Add((pageIndex, topLeft, color, shaped));
        }
    }
}
