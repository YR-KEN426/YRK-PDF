using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Export;

/// <summary>A picture's file for Word: "png" or "jpeg".</summary>
public sealed record DocxImage(byte[] Bytes, string Format, int PixelWidth, int PixelHeight);

/// <summary>
/// One PDF page for Word: its paragraphs, tables and pictures (<see cref="Blocks"/>, from <see cref="PageLayout"/>)
/// with the pictures' files in <see cref="Images"/>; or only <see cref="Paragraphs"/>; or a picture of the whole
/// page when it has no text (a scan).
/// </summary>
public sealed record DocxPage(
    double Width,
    double Height,
    IReadOnlyList<TextParagraph> Paragraphs,
    DocxImage? Picture = null,
    IReadOnlyList<LayoutBlock>? Blocks = null,
    IReadOnlyDictionary<LayoutPicture, DocxImage>? Images = null);

/// <summary>
/// Writes an editable Word document (.docx): paragraphs with font, size, bold, italic and colour, tables with
/// merged cells, borders and cell colours, and pictures, one PDF page per Word page. Pages keep the PDF's layout:
/// text keeps its line spacing and the space between blocks, and pictures float where they were.
/// No library: the package is a ZIP of a few XML parts.
/// </summary>
public static class DocxWriter
{
    private const double DefaultMargin = 54; // Points (0.75 inch).

    /// <summary>
    /// The paragraph a cell with no text must still hold, one point tall: Word's usual empty line (some 13 points)
    /// would make the thin rows of a chart's frame, and so the page, taller than on the PDF.
    /// </summary>
    private const string EmptyCellParagraph =
        "<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"20\" w:lineRule=\"exact\"/><w:rPr><w:sz w:val=\"2\"/><w:szCs w:val=\"2\"/></w:rPr></w:pPr></w:p>";
    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static void Write(Stream output, IReadOnlyList<DocxPage> pages)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var margins = Margins(pages);
        var writer = new BodyWriter(zip, margins);
        for (var p = 0; p < pages.Count; p++)
        {
            writer.Page(pages[p], pageBreak: p > 0);
        }

        var first = pages.Count > 0 ? pages[0] : new DocxPage(595, 842, []);
        var body = writer.Body;
        body.Append(CultureInfo.InvariantCulture,
            $"<w:sectPr><w:pgSz w:w=\"{Twips(first.Width)}\" w:h=\"{Twips(first.Height)}\"{(first.Width > first.Height ? " w:orient=\"landscape\"" : string.Empty)}/>" +
            $"<w:pgMar w:top=\"{Twips(margins.Top)}\" w:right=\"{Twips(margins.Right)}\" w:bottom=\"{Twips(margins.Bottom)}\" w:left=\"{Twips(margins.Left)}\" w:header=\"0\" w:footer=\"0\" w:gutter=\"0\"/></w:sectPr>");

        OfficeXml.AddPart(zip, "[Content_Types].xml", OfficeXml.Header +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
            "<Default Extension=\"jpg\" ContentType=\"image/jpeg\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
            "</Types>");
        OfficeXml.AddPart(zip, "_rels/.rels", OfficeXml.Header +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{RelNs}/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>");
        OfficeXml.AddPart(zip, "word/_rels/document.xml.rels", OfficeXml.Header +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rIdStyles\" Type=\"{RelNs}/styles\" Target=\"styles.xml\"/>" +
            writer.Relationships +
            "</Relationships>");
        OfficeXml.AddPart(zip, "word/styles.xml", OfficeXml.Header +
            $"<w:styles xmlns:w=\"{WordNs}\"><w:docDefaults>" +
            "<w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Tahoma\" w:eastAsia=\"Calibri\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"en-US\" w:bidi=\"th-TH\"/></w:rPr></w:rPrDefault>" +
            "<w:pPrDefault><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault>" +
            "</w:docDefaults></w:styles>");
        OfficeXml.AddPart(zip, "word/document.xml", OfficeXml.Header +
            $"<w:document xmlns:w=\"{WordNs}\" xmlns:r=\"{RelNs}\" " +
            "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
            "<w:body>" + body + "</w:body></w:document>");
    }

    /// <summary>
    /// Page margins (points) that put the text where it starts on the PDF pages. The right one is kept narrow:
    /// Word's fonts often set Thai a little wider than the PDF, and lines should not wrap earlier than there.
    /// </summary>
    private sealed record PageMargins(double Left, double Right, double Top, double Bottom);

    private static PageMargins Margins(IReadOnlyList<DocxPage> pages)
    {
        var laidOut = pages.Where(p => p.Blocks is { Count: > 0 }).ToList();
        var flow = laidOut.SelectMany(p => p.Blocks!.Except(Footers(p.Blocks!, p.Height)).Where(b => b is not LayoutPicture).Select(b => (Page: p, Block: b))).ToList();
        if (flow.Count == 0)
        {
            return new PageMargins(DefaultMargin, DefaultMargin, DefaultMargin, DefaultMargin);
        }

        return new PageMargins(
            Math.Clamp(flow.Min(f => f.Block.Left), 18, 72),
            Math.Clamp(flow.Min(f => f.Block.Left) / 2, 14, 24),
            Math.Clamp(flow.Min(f => f.Block.Top) - 2, 14, 54),
            Math.Clamp(flow.Min(f => f.Page.Height - WordBottom(f.Block)) - 10, 0, 54));
    }

    /// <summary>Text at the foot of the page (page numbers, a report title): kept there whatever happens above it.</summary>
    /// <remarks>Lines in the last tenth of the page, below a clear gap: body text running down there is not one.</remarks>
    private static HashSet<LayoutBlock> Footers(IReadOnlyList<LayoutBlock> blocks, double pageHeight)
    {
        var footers = new HashSet<LayoutBlock>();
        var text = blocks.Where(b => b is not LayoutPicture).OrderBy(b => b.Top).ToList();
        for (var i = text.Count - 1; i >= 0 && text[i] is LayoutParagraph { Paragraph: var p } && p.Top > pageHeight * 0.9; i--)
        {
            if (i > 0 && p.Top - text[i - 1].Bottom < p.FontSize)
            {
                break; // Continues the text above it.
            }

            footers.Add(text[i]);
        }

        return footers;
    }

    /// <summary>Where a block ends in Word: a paragraph takes whole lines, a little taller than its letters on the PDF.</summary>
    private static double WordBottom(LayoutBlock block) => block is LayoutParagraph { Paragraph: var p }
        ? p.Top + (p.LineCount * Math.Max(p.LinePitch, p.FontSize * 1.25))
        : block.Bottom;

    private sealed class BodyWriter(ZipArchive zip, PageMargins margins)
    {
        private int _pictures;

        public StringBuilder Body { get; } = new();

        public StringBuilder Relationships { get; } = new();

        public void Page(DocxPage page, bool pageBreak)
        {
            var contentWidth = page.Width - margins.Left - margins.Right;
            var maxHeight = page.Height - margins.Top - margins.Bottom - 6;
            if (page.Picture is { } picture)
            {
                Body.Append(InlinePicture(picture, contentWidth, maxHeight, 0, pageBreak, center: true, spaceBefore: 0));
                return;
            }

            if (page.Blocks is { Count: > 0 } blocks)
            {
                LaidOutPage(blocks, page.Images, page.Width, page.Height, contentWidth, maxHeight, pageBreak);
                return;
            }

            if (page.Paragraphs.Count == 0)
            {
                Body.Append(pageBreak ? "<w:p><w:pPr><w:pageBreakBefore/></w:pPr></w:p>" : "<w:p/>");
                return;
            }

            foreach (var paragraph in page.Paragraphs)
            {
                Body.Append(Paragraph(paragraph, pageBreak, paragraph.SpaceBefore, paragraph.Indent, null));
                pageBreak = false;
            }
        }

        /// <summary>
        /// A page as on the PDF: pictures float at their place on the page; paragraphs and tables follow each
        /// other with the PDF's gaps and line spacing, so they stay level with the pictures.
        /// </summary>
        private void LaidOutPage(
            IReadOnlyList<LayoutBlock> blocks, IReadOnlyDictionary<LayoutPicture, DocxImage>? images, double pageWidth, double pageHeight, double width, double maxHeight, bool pageBreak)
        {
            // A one-point paragraph starts the page and holds its floating pictures.
            Body.Append("<w:p><w:pPr>").Append(pageBreak ? "<w:pageBreakBefore/>" : string.Empty)
                .Append("<w:spacing w:before=\"0\" w:after=\"0\" w:line=\"20\" w:lineRule=\"exact\"/></w:pPr>");
            foreach (var picture in blocks.OfType<LayoutPicture>())
            {
                if (images is not null && images.TryGetValue(picture, out var image))
                {
                    Body.Append(FloatingPicture(image, picture));
                }
            }

            Body.Append("</w:p>");

            // Footer lines sit in frames at their place on the page, so a fuller page in Word cannot push them away.
            var footers = Footers(blocks, pageHeight);
            foreach (var footer in footers.OfType<LayoutParagraph>())
            {
                var p = footer.Paragraph;
                var frame = FormattableString.Invariant(
                    $"<w:framePr w:w=\"{Twips(pageWidth - p.Left - 8)}\" w:hAnchor=\"page\" w:vAnchor=\"page\" w:x=\"{Twips(p.Left)}\" w:y=\"{Twips(Math.Max(0, p.Top - 4))}\" w:wrap=\"none\"/>");
                Body.Append(Paragraph(p, false, 0, 0, null, tabOrigin: p.Left, frame: frame));
            }

            var pitches = LinePitches(blocks.OfType<LayoutParagraph>().Select(b => b.Paragraph).ToList());
            var flowY = margins.Top + 1;
            var flow = blocks.Where(b => b is not LayoutPicture && !footers.Contains(b)).ToList();
            for (var index = 0; index < flow.Count; index++)
            {
                var block = flow[index];
                var space = Math.Max(0, block.Top - flowY);
                var indent = Math.Clamp(block.Left - margins.Left, 0, Math.Max(0, width - 24));
                double height;
                switch (block)
                {
                    case LayoutParagraph { Paragraph: var paragraph }:
                        var pitch = paragraph.LinePitch > 0 ? paragraph.LinePitch : pitches.GetValueOrDefault(SizeKey(paragraph.FontSize));

                        // A single line followed sooner than the page's usual line distance (a note under a table, the line
                        // before one) takes only that room, or the rest of the page moves down and can run over.
                        if (paragraph.LineCount == 1 && index + 1 < flow.Count && flow[index + 1].Top - block.Top is var gap
                            && gap < pitch && gap >= paragraph.FontSize * 1.05)
                        {
                            pitch = gap;
                        }

                        double? exact = pitch >= paragraph.FontSize * 1.05 ? pitch : null;
                        var aligned = paragraph.Centered || paragraph.RightAligned;
                        var (rightIndent, scale) = paragraph.Centered ? (0, 100) : KeepOnOneLine(paragraph, pageWidth);
                        Body.Append(Paragraph(paragraph, false, space, aligned ? 0 : indent, exact, rightIndent, scale, tabOrigin: margins.Left));
                        height = paragraph.LineCount * (exact ?? (paragraph.FontSize * 1.35));
                        break;
                    case LayoutTable table:
                        // A tiny paragraph before the table carries the space above it.
                        Body.Append(CultureInfo.InvariantCulture,
                            $"<w:p><w:pPr><w:spacing w:before=\"{Twips(Math.Max(0, space - 1))}\" w:after=\"0\" w:line=\"20\" w:lineRule=\"exact\"/></w:pPr></w:p>");
                        height = Table(table, images, width, maxHeight, indent) + 1; // With the one-point paragraph before it.
                        break;
                    default:
                        continue;
                }

                // Where Word has got to: a block that cannot start as high as on the PDF pushes the rest down,
                // and the next gap takes it back.
                flowY += space + height;
            }
        }

        /// <summary>
        /// A line that is one line in the PDF should stay one line, though Word's fonts often set Thai a little wider:
        /// it may reach into the right margin, and is squeezed slightly when even that is not enough.
        /// </summary>
        private (double RightIndent, int Scale) KeepOnOneLine(TextParagraph paragraph, double pageWidth)
        {
            // A font Word puts in for a missing one can be some 15% wider: such lines may use the margin too.
            var width = paragraph.Right - paragraph.Left;
            if (paragraph.LineCount != 1 || paragraph.Right <= 0 || width * 1.15 <= pageWidth - margins.Right - paragraph.Left)
            {
                return (0, 100);
            }

            var need = width * 1.06;
            var room = pageWidth - 4 - paragraph.Left;
            return (-(margins.Right - 4), need <= room ? 100 : Math.Clamp((int)(100 * room / need), 80, 99));
        }

        /// <summary>The usual distance between lines of each text size on the page, from paragraphs of several lines and from runs of single lines.</summary>
        private static Dictionary<double, double> LinePitches(List<TextParagraph> paragraphs)
        {
            var samples = new List<(double Size, double Pitch)>();
            foreach (var p in paragraphs.Where(p => p.LineCount > 1 && p.LinePitch > 0))
            {
                samples.Add((SizeKey(p.FontSize), p.LinePitch));
            }

            for (var i = 1; i < paragraphs.Count; i++)
            {
                var (a, b) = (paragraphs[i - 1], paragraphs[i]);
                var distance = b.Top - a.Top - ((a.LineCount - 1) * Math.Max(a.LinePitch, 0));
                if (Math.Abs(a.FontSize - b.FontSize) <= 0.5 && distance >= a.FontSize && distance <= a.FontSize * 1.8)
                {
                    samples.Add((SizeKey(a.FontSize), distance));
                }
            }

            return samples.GroupBy(s => s.Size).ToDictionary(g => g.Key, g => g.Select(s => s.Pitch).Order().ElementAt(g.Count() / 2));
        }

        private static double SizeKey(double size) => Math.Round(size * 2) / 2;

        /// <summary>Blocks in a table cell, top to bottom; indents are measured from <paramref name="left"/>.</summary>
        /// <summary>
        /// Word ignores "no wrap" in a table of fixed column widths, so a one-line cell whose text fills most of its
        /// width on the PDF would wrap in a font Word puts in for a missing one, and make its row taller: such text
        /// is narrowed a little (character width in percent), leaving room for a font about an eighth wider.
        /// </summary>
        private static int CellScale(TextParagraph paragraph, double room)
        {
            var width = paragraph.Right - paragraph.Left;
            if (paragraph.LineCount != 1 || width <= 0 || room <= 0 || width * 1.12 <= room)
            {
                return 100;
            }

            return Math.Clamp((int)(100 * room / (width * 1.12)), 75, 100);
        }

        private void CellBlocks(
            IReadOnlyList<LayoutBlock> blocks, IReadOnlyDictionary<LayoutPicture, DocxImage>? images, double width, double height, double maxHeight, double left)
        {
            // Lines as tall as in the PDF, so rows keep their height (Word's own line height for Thai fonts is larger).
            var lines = blocks.OfType<LayoutParagraph>().Sum(p => p.Paragraph.LineCount);
            var pictureHeight = blocks.OfType<LayoutPicture>().Sum(p => p.Height);
            var fitting = lines > 0 ? (height - pictureHeight - 3) / lines : 0; // Room for the borders.
            double? flowY = null;
            var wrote = false;
            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                var space = flowY is { } y ? Math.Max(0, block.Top - y) : 0;
                var indent = Math.Clamp(block.Left - left, 0, Math.Max(0, width - 24));
                double blockHeight;
                switch (block)
                {
                    case LayoutParagraph { Paragraph: var paragraph }:
                        // The PDF's line distance when the cell shows it (several lines), else what fits the cell.
                        var next = i + 1 < blocks.Count ? blocks[i + 1].Top - block.Top : 0;
                        var pitch = paragraph.LinePitch > 0 ? paragraph.LinePitch
                            : next > paragraph.FontSize && next < paragraph.FontSize * 1.6 ? next
                            : fitting;
                        pitch = Math.Clamp(Math.Min(pitch, fitting), paragraph.FontSize * 1.05, paragraph.FontSize * 1.5);
                        var aligned = paragraph.Centered || paragraph.RightAligned;
                        Body.Append(Paragraph(paragraph, false, space, aligned ? 0 : indent, pitch, scale: CellScale(paragraph, width - (aligned ? 0 : indent)), tabOrigin: left));
                        blockHeight = paragraph.LineCount * pitch;
                        break;
                    case LayoutPicture picture when images is not null && images.TryGetValue(picture, out var image):
                        Body.Append(InlinePicture(image, Math.Min(picture.Width, width - indent), maxHeight, indent, false, center: false, space));
                        blockHeight = picture.Height;
                        break;
                    default:
                        continue;
                }

                flowY = (flowY is null ? block.Top : flowY.Value + space) + blockHeight;
                wrote = true;
            }

            if (!wrote)
            {
                Body.Append(EmptyCellParagraph); // A cell must hold a paragraph.
            }
        }

        /// <returns>The table's height in Word: rows too low for a line of their text grow.</returns>
        private double Table(LayoutTable table, IReadOnlyDictionary<LayoutPicture, DocxImage>? images, double width, double maxHeight, double indent)
        {
            // Narrower than the page when needed, keeping the columns' proportions.
            var scale = Math.Min(1, (width - indent) / table.Width);
            var columns = table.ColumnWidths.Select(w => w * scale).ToList();
            Body.Append(CultureInfo.InvariantCulture,
                $"<w:tbl><w:tblPr><w:tblW w:w=\"{Twips(columns.Sum())}\" w:type=\"dxa\"/><w:tblInd w:w=\"{Twips(indent)}\" w:type=\"dxa\"/>");
            Body.Append("<w:tblLayout w:type=\"fixed\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"40\" w:type=\"dxa\"/>")
                .Append("<w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"40\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr><w:tblGrid>");
            foreach (var column in columns)
            {
                Body.Append(CultureInfo.InvariantCulture, $"<w:gridCol w:w=\"{Twips(column)}\"/>");
            }

            Body.Append("</w:tblGrid>");
            var cellAt = new Dictionary<(int, int), LayoutCell>();
            foreach (var cell in table.Cells)
            {
                for (var r = cell.Row; r < cell.Row + cell.RowSpan; r++)
                {
                    for (var c = cell.Column; c < cell.Column + cell.ColumnSpan; c++)
                    {
                        cellAt[(r, c)] = cell;
                    }
                }
            }

            var total = 0.0;
            for (var r = 0; r < table.RowHeights.Count; r++)
            {
                // A row holds at least its one-row cells' lines (cell text is set at no less than 1.05 times its size).
                var row = r;
                var needed = table.Cells.Where(c => c.Row == row && c.RowSpan == 1)
                    .Select(c => c.Content.OfType<LayoutParagraph>().Sum(p => p.Paragraph.LineCount * p.Paragraph.FontSize * 1.05) + 1.5)
                    .DefaultIfEmpty(0).Max();
                var rowHeight = Math.Max(table.RowHeights[r] * scale, needed);
                total += rowHeight;
                Body.Append(CultureInfo.InvariantCulture,
                    $"<w:tr><w:trPr><w:cantSplit/><w:trHeight w:val=\"{Twips(rowHeight)}\" w:hRule=\"atLeast\"/></w:trPr>");
                for (var c = 0; c < columns.Count;)
                {
                    if (!cellAt.TryGetValue((r, c), out var cell))
                    {
                        Body.Append(CultureInfo.InvariantCulture, $"<w:tc><w:tcPr><w:tcW w:w=\"{Twips(columns[c])}\" w:type=\"dxa\"/></w:tcPr>{EmptyCellParagraph}</w:tc>");
                        c++;
                        continue;
                    }

                    var span = Math.Min(cell.ColumnSpan, columns.Count - c);
                    var cellWidth = columns.Skip(c).Take(span).Sum();
                    Body.Append("<w:tc><w:tcPr>");
                    Body.Append(CultureInfo.InvariantCulture, $"<w:tcW w:w=\"{Twips(cellWidth)}\" w:type=\"dxa\"/>");
                    if (span > 1)
                    {
                        Body.Append(CultureInfo.InvariantCulture, $"<w:gridSpan w:val=\"{span}\"/>");
                    }

                    if (cell.RowSpan > 1)
                    {
                        Body.Append(cell.Row == r ? "<w:vMerge w:val=\"restart\"/>" : "<w:vMerge/>");
                    }

                    Body.Append(Borders(cell.Borders));
                    if (cell.Fill is { } fill)
                    {
                        Body.Append(CultureInfo.InvariantCulture, $"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{fill.R:X2}{fill.G:X2}{fill.B:X2}\"/>");
                    }

                    // Text that is one line in the PDF stays one line, cut at the cell's edge as a spreadsheet prints it,
                    // so long names do not make rows taller.
                    if (cell.Content.OfType<LayoutParagraph>().All(p => p.Paragraph.LineCount == 1))
                    {
                        Body.Append("<w:noWrap/>");
                    }

                    Body.Append("<w:vAlign w:val=\"center\"/></w:tcPr>");
                    if (cell.Row == r)
                    {
                        var cellLeft = table.X + table.ColumnWidths.Take(cell.Column).Sum() + 2;
                        CellBlocks(cell.Content, images, cellWidth - 4, table.RowHeights.Skip(cell.Row).Take(cell.RowSpan).Sum() * scale, maxHeight, cellLeft);
                    }
                    else
                    {
                        Body.Append(EmptyCellParagraph);
                    }

                    Body.Append("</w:tc>");
                    c += span;
                }

                Body.Append("</w:tr>");
            }

            Body.Append("</w:tbl>");
            return total;
        }

        private static string Borders(CellBorders borders)
        {
            static string Side(string name, bool on) =>
                on ? $"<w:{name} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"000000\"/>" : $"<w:{name} w:val=\"nil\"/>";

            return "<w:tcBorders>" + Side("top", borders.HasFlag(CellBorders.Top)) + Side("left", borders.HasFlag(CellBorders.Left))
                + Side("bottom", borders.HasFlag(CellBorders.Bottom)) + Side("right", borders.HasFlag(CellBorders.Right)) + "</w:tcBorders>";
        }

        /// <summary>Adds the picture's file to the package; returns its number (for ids and names).</summary>
        private int AddImage(DocxImage image)
        {
            var number = ++_pictures;
            var extension = image.Format == "jpeg" ? "jpg" : "png";
            var target = $"media/image{number}.{extension}";
            OfficeXml.AddPart(zip, "word/" + target, image.Bytes);
            Relationships.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rIdPic{number}\" Type=\"{RelNs}/image\" Target=\"{target}\"/>");
            return number;
        }

        /// <summary>A picture in front of the text at its place on the page (points from the page's top-left).</summary>
        private string FloatingPicture(DocxImage image, LayoutPicture picture)
        {
            var number = AddImage(image);
            long x = Emu(picture.X), y = Emu(picture.Y), cx = Emu(picture.Width), cy = Emu(picture.Height);

            // Integers only: no culture-specific formatting involved.
            return
                "<w:r><w:drawing>" +
                $"<wp:anchor distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" simplePos=\"0\" relativeHeight=\"{number}\" behindDoc=\"0\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">" +
                "<wp:simplePos x=\"0\" y=\"0\"/>" +
                $"<wp:positionH relativeFrom=\"page\"><wp:posOffset>{x}</wp:posOffset></wp:positionH>" +
                $"<wp:positionV relativeFrom=\"page\"><wp:posOffset>{y}</wp:posOffset></wp:positionV>" +
                $"<wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapNone/>" +
                $"<wp:docPr id=\"{number}\" name=\"Picture {number}\"/>" +
                Graphic(number, cx, cy) +
                "</wp:anchor></w:drawing></w:r>";
        }

        private string InlinePicture(DocxImage image, double width, double maxHeight, double indent, bool pageBreakBefore, bool center, double spaceBefore)
        {
            var number = AddImage(image);

            // Fit inside the space, keeping the proportions.
            var aspect = image.PixelWidth > 0 && image.PixelHeight > 0 ? (double)image.PixelHeight / image.PixelWidth : 1;
            var height = width * aspect;
            if (height > maxHeight)
            {
                height = maxHeight;
                width = height / aspect;
            }

            long cx = Emu(width), cy = Emu(height);
            var properties = new StringBuilder("<w:pPr>");
            if (pageBreakBefore)
            {
                properties.Append("<w:pageBreakBefore/>");
            }

            properties.Append(CultureInfo.InvariantCulture, $"<w:spacing w:before=\"{Twips(spaceBefore)}\" w:after=\"0\"/>");
            if (indent > 1)
            {
                properties.Append(CultureInfo.InvariantCulture, $"<w:ind w:left=\"{Twips(indent)}\"/>");
            }

            if (center)
            {
                properties.Append("<w:jc w:val=\"center\"/>");
            }

            properties.Append("</w:pPr>");
            return
                $"<w:p>{properties}<w:r><w:drawing>" +
                $"<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/>" +
                $"<wp:docPr id=\"{number}\" name=\"Picture {number}\"/>" +
                Graphic(number, cx, cy) +
                "</wp:inline></w:drawing></w:r></w:p>";
        }

        private static string Graphic(int number, long cx, long cy) =>
            "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
            $"<pic:pic><pic:nvPicPr><pic:cNvPr id=\"{number}\" name=\"image{number}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
            $"<pic:blipFill><a:blip r:embed=\"rIdPic{number}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
            $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
            "</pic:pic></a:graphicData></a:graphic>";

        private static long Emu(double points) => (long)Math.Round(points * 12700);
    }

    /// <param name="exactLine">The distance between lines in points, as on the PDF; null for Word's own.</param>
    /// <param name="rightIndent">Negative to let the line reach into the right margin.</param>
    /// <param name="scale">Character width in percent (100 = normal).</param>
    private static string Paragraph(
        TextParagraph paragraph, bool pageBreakBefore, double spaceBefore, double indent, double? exactLine, double rightIndent = 0, int scale = 100, double? tabOrigin = null, string? frame = null)
    {
        var xml = new StringBuilder("<w:p><w:pPr>");
        if (pageBreakBefore)
        {
            xml.Append("<w:pageBreakBefore/>");
        }

        xml.Append(frame);

        // Tab stops where the text after each wide gap starts on the PDF (measured from <paramref name="tabOrigin"/>).
        var stops = tabOrigin is { } origin
            ? paragraph.Runs.Where(r => r.IsTab && r.TabTo > origin + 4).Select(r => Twips(r.TabTo - origin)).Distinct().Order().ToList()
            : [];
        if (stops.Count > 0)
        {
            xml.Append("<w:tabs>");
            foreach (var stop in stops)
            {
                xml.Append(CultureInfo.InvariantCulture, $"<w:tab w:val=\"left\" w:pos=\"{stop}\"/>");
            }

            xml.Append("</w:tabs>");
        }

        xml.Append(CultureInfo.InvariantCulture, $"<w:spacing w:before=\"{Twips(spaceBefore)}\" w:after=\"0\"");
        xml.Append(exactLine is { } line ? $" w:line=\"{Twips(line)}\" w:lineRule=\"exact\"/>" : "/>");
        if (indent > 1 || rightIndent != 0)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<w:ind w:left=\"{Twips(Math.Max(0, indent))}\" w:right=\"{Twips(rightIndent)}\"/>");
        }

        if (paragraph.Centered)
        {
            xml.Append("<w:jc w:val=\"center\"/>");
        }
        else if (paragraph.RightAligned)
        {
            xml.Append("<w:jc w:val=\"right\"/>");
        }

        xml.Append("</w:pPr>");
        string? previous = null;
        foreach (var run in paragraph.Runs)
        {
            // Where a PDF line ended between two Thai words, an invisible break mark (zero-width space) lets Word
            // break there too; Word's own Thai dictionary often breaks earlier, adding lines that push the page over.
            var text = run.StartsLine && previous is { Length: > 0 } && run.Text.Length > 0 && IsThai(previous[^1]) && IsThai(run.Text[0])
                ? "​" + run.Text
                : run.Text;
            xml.Append("<w:r>").Append(RunProperties(run, scale));
            xml.Append(run.IsTab ? "<w:tab/>" : $"<w:t xml:space=\"preserve\">{OfficeXml.Escape(text)}</w:t>");
            xml.Append("</w:r>");
            previous = run.Text;
        }

        return xml.Append("</w:p>").ToString();
    }

    private static string RunProperties(TextRun run, int scale = 100)
    {
        var xml = new StringBuilder("<w:rPr>");
        if (OfficeXml.FontFamily(run.FontName) is { } font)
        {
            var name = OfficeXml.Escape(font);
            xml.Append(CultureInfo.InvariantCulture, $"<w:rFonts w:ascii=\"{name}\" w:hAnsi=\"{name}\" w:cs=\"{name}\"/>");
        }

        if (run.Bold)
        {
            xml.Append("<w:b/><w:bCs/>");
        }

        if (run.Italic)
        {
            xml.Append("<w:i/><w:iCs/>");
        }

        if (run.Color != new PdfColor(0, 0, 0))
        {
            xml.Append(CultureInfo.InvariantCulture, $"<w:color w:val=\"{run.Color.R:X2}{run.Color.G:X2}{run.Color.B:X2}\"/>");
        }

        if (scale != 100)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<w:w w:val=\"{scale}\"/>");
        }

        var halfPoints = Math.Clamp((int)Math.Round(run.FontSize * 2), 2, 3276);
        xml.Append(CultureInfo.InvariantCulture, $"<w:sz w:val=\"{halfPoints}\"/><w:szCs w:val=\"{halfPoints}\"/>");
        return xml.Append("</w:rPr>").ToString();
    }

    private static int Twips(double points) => (int)Math.Round(points * 20);

    private static bool IsThai(char c) => c is >= '฀' and <= '๿';
}
