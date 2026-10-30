using System.Globalization;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Archive;
using Yrk.PdfStudio.Core.Pdf.ETax;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>
/// Lays out an e-Tax Invoice on A4 pages in Sarabun (real, searchable Thai text) and saves it as PDF/A-3 with its
/// ETDA XML inside, ready to be signed with a digital ID.
/// </summary>
internal static class TaxInvoiceRenderer
{
    public const string XmlFileName = "ETDA-invoice.xml";

    private const double PageWidth = 595.28;
    private const double PageHeight = 841.89;
    private const double Margin = 40;
    private const double Right = PageWidth - Margin;
    private const string Font = "Sarabun";
    private const double Body = 10.5;
    private const double RowPadding = 4;
    private const double FooterTop = PageHeight - 150; // Totals and signatures need the space below.

    private static readonly CultureInfo Thai = CreateThaiCulture();

    // Table columns: no. | description | quantity | unit | unit price | amount (left edges, then the right edge).
    private static readonly double[] Columns = [Margin, Margin + 30, Right - 265, Right - 205, Right - 160, Right - 85, Right];

    public static byte[] Render(TaxInvoice invoice, DateTimeOffset created)
    {
        var pages = new List<List<PdfStroke>>();
        var text = new List<(int PageIndex, NormalizedPoint TopLeft, PdfColor Color, TextShaper.ShapedLine Line)>();
        var black = new PdfColor(0, 0, 0);
        var gray = new PdfColor(0x55, 0x55, 0x55);

        // Writes one line of text with its top at y; align 0 = left of x, 1 = right of x, 0.5 = centred on x.
        double Write(int page, string value, double x, double y, double size = Body, bool bold = false, double align = 0, PdfColor? color = null, double wrap = 0)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return 0;
            }

            var line = TextShaper.Shape(value, Font, bold, italic: false, size, wrap);
            text.Add((page, new NormalizedPoint((x - (line.Width * align)) / PageWidth, y / PageHeight), color ?? black, line));
            return line.Height;
        }

        double Height(string value, double width, double size = Body) =>
            TextShaper.Shape(value.Length == 0 ? " " : value, Font, bold: false, italic: false, size, width).Height;

        // --- Header, on every page ---------------------------------------------------------
        double Header(int page)
        {
            var strokes = new List<PdfStroke>();
            pages.Add(strokes);
            var y = Margin;
            Write(page, invoice.ThaiName, Right, y, 18, bold: true, align: 1);
            Write(page, page == 0 ? "ต้นฉบับ" : $"หน้า {page + 1}", Right, y + 26, Body, align: 1, color: gray);
            y += Write(page, invoice.Seller.Name, Margin, y, 15, bold: true);
            foreach (var line in AddressLines(invoice.Seller))
            {
                y += Write(page, line, Margin, y, wrap: 300);
            }

            y += Write(page, $"เลขประจำตัวผู้เสียภาษีอากร {FormatId(invoice.Seller.Id)}  {Branch(invoice.Seller.Branch)}", Margin, y);
            var contact = string.Join("  ", new[] { invoice.Seller.Phone is { Length: > 0 } p ? $"โทร. {p}" : null, invoice.Seller.Email }.Where(s => !string.IsNullOrWhiteSpace(s)));
            y += Write(page, contact, Margin, y);
            y += 10;

            // Buyer on the left, number and date on the right, in one box.
            var top = y;
            var split = Right - 170;
            var by = top + 6;
            by += Write(page, "ลูกค้า", Margin + 6, by, Body, bold: true);
            by += Write(page, invoice.Buyer.Name, Margin + 6, by, wrap: split - Margin - 12);
            foreach (var line in AddressLines(invoice.Buyer))
            {
                by += Write(page, line, Margin + 6, by, wrap: split - Margin - 12);
            }

            var buyerId = invoice.Buyer.IdKind switch
            {
                TaxIdKind.TaxId => $"เลขประจำตัวผู้เสียภาษีอากร {FormatId(invoice.Buyer.Id)}  {Branch(invoice.Buyer.Branch)}",
                TaxIdKind.NationalId => $"เลขประจำตัวประชาชน {FormatId(invoice.Buyer.Id)}",
                TaxIdKind.Passport => $"หนังสือเดินทาง {invoice.Buyer.Id.Trim()}",
                _ => string.Empty,
            };
            by += Write(page, buyerId, Margin + 6, by);
            var ry = top + 6;
            Write(page, "เลขที่", split + 8, ry, bold: true);
            ry += Write(page, invoice.Number, Right - 6, ry, align: 1);
            Write(page, "วันที่", split + 8, ry, bold: true);
            ry += Write(page, invoice.IssueDate.ToString("d MMMM yyyy", Thai), Right - 6, ry, align: 1);
            var bottom = Math.Max(by, ry) + 6;
            strokes.Add(new PdfStroke(Margin, top, Right, top));
            strokes.Add(new PdfStroke(Margin, bottom, Right, bottom));
            strokes.Add(new PdfStroke(Margin, top, Margin, bottom));
            strokes.Add(new PdfStroke(Right, top, Right, bottom));
            strokes.Add(new PdfStroke(split, top, split, bottom));
            y = bottom + 12;

            // Table header.
            var headerBottom = y + 20;
            strokes.Add(new PdfStroke(Margin, y, Right, headerBottom, Fill: 0.92));
            string[] titles = ["ลำดับ", "รายการ", "จำนวน", "หน่วย", "ราคาต่อหน่วย", "จำนวนเงิน"];
            for (var c = 0; c < titles.Length; c++)
            {
                Write(page, titles[c], (Columns[c] + Columns[c + 1]) / 2, y + 3, bold: true, align: 0.5);
            }

            strokes.Add(new PdfStroke(Margin, y, Right, y));
            strokes.Add(new PdfStroke(Margin, headerBottom, Right, headerBottom));
            return headerBottom;
        }

        void CloseTable(int page, double top, double bottom)
        {
            foreach (var x in Columns)
            {
                pages[page].Add(new PdfStroke(x, top, x, bottom));
            }

            pages[page].Add(new PdfStroke(Margin, bottom, Right, bottom));
        }

        // --- Items ---------------------------------------------------------------------------
        var pageIndex = 0;
        var tableTop = Header(0) - 20;
        var y = tableTop + 20;
        for (var i = 0; i < invoice.Lines.Count; i++)
        {
            var line = invoice.Lines[i];
            var descriptionWidth = Columns[2] - Columns[1] - 8;
            var rowHeight = Height(line.Description, descriptionWidth) + (2 * RowPadding);
            var isLast = i == invoice.Lines.Count - 1;
            if (y + rowHeight > (isLast ? FooterTop : PageHeight - Margin - 20))
            {
                CloseTable(pageIndex, tableTop, y);
                pageIndex++;
                tableTop = Header(pageIndex) - 20;
                y = tableTop + 20;
            }

            var ty = y + RowPadding;
            Write(pageIndex, (i + 1).ToString(CultureInfo.InvariantCulture), (Columns[0] + Columns[1]) / 2, ty, align: 0.5);
            Write(pageIndex, line.Description, Columns[1] + 4, ty, wrap: descriptionWidth);
            Write(pageIndex, Number(line.Quantity, line.Quantity % 1 == 0 ? 0 : 2), Columns[3] - 4, ty, align: 1);
            Write(pageIndex, line.Unit, (Columns[3] + Columns[4]) / 2, ty, align: 0.5);
            Write(pageIndex, Number(line.UnitPrice), Columns[5] - 4, ty, align: 1);
            Write(pageIndex, Number(line.Amount), Columns[6] - 4, ty, align: 1);
            y += rowHeight;
        }

        if (y > FooterTop - 10)
        {
            CloseTable(pageIndex, tableTop, y);
            pageIndex++;
            tableTop = Header(pageIndex) - 20;
            y = tableTop + 20;
        }

        // The table runs down to the totals, as on a printed form.
        var tableBottom = Math.Max(y, FooterTop - 60);
        CloseTable(pageIndex, tableTop, tableBottom);

        // --- Totals, amount in words, note and signatures --------------------------------------
        var vat = invoice.VatRate.ToString("0.##", CultureInfo.InvariantCulture);
        (string Label, decimal Value)[] totals = invoice.PricesIncludeVat
            ? [("มูลค่าสินค้า/บริการ (ไม่รวมภาษี)", invoice.TaxBasis), ($"ภาษีมูลค่าเพิ่ม {vat}%", invoice.Vat), ("จำนวนเงินรวมทั้งสิ้น", invoice.GrandTotal)]
            : [("รวมเป็นเงิน", invoice.LineTotal), ($"ภาษีมูลค่าเพิ่ม {vat}%", invoice.Vat), ("จำนวนเงินรวมทั้งสิ้น", invoice.GrandTotal)];
        var ty2 = tableBottom;
        for (var t = 0; t < totals.Length; t++)
        {
            var last = t == totals.Length - 1;
            if (last)
            {
                pages[pageIndex].Add(new PdfStroke(Columns[3], ty2, Right, ty2 + 20, Fill: 0.92));
            }

            Write(pageIndex, totals[t].Label, Columns[5] - 4, ty2 + 3, bold: last, align: 1);
            Write(pageIndex, Number(totals[t].Value), Right - 4, ty2 + 3, bold: last, align: 1);
            ty2 += 20;
            pages[pageIndex].Add(new PdfStroke(Columns[3], ty2, Right, ty2));
        }

        pages[pageIndex].Add(new PdfStroke(Columns[3], tableBottom, Columns[3], ty2));
        pages[pageIndex].Add(new PdfStroke(Columns[5], tableBottom, Columns[5], ty2));
        pages[pageIndex].Add(new PdfStroke(Right, tableBottom, Right, ty2));
        pages[pageIndex].Add(new PdfStroke(Margin, tableBottom + 40, Columns[3] - 10, tableBottom + 40 + 20, Fill: 0.95));
        Write(pageIndex, $"({AmountInWords.Thai(invoice.GrandTotal)})", (Margin + Columns[3] - 10) / 2, tableBottom + 43, bold: true, align: 0.5);
        if (!string.IsNullOrWhiteSpace(invoice.Note))
        {
            Write(pageIndex, "หมายเหตุ: " + invoice.Note.Trim(), Margin, tableBottom + 66, wrap: Columns[3] - Margin - 10);
        }

        var signY = PageHeight - Margin - 30;
        foreach (var (center, label) in new[] { (Margin + 110, "ผู้รับเงิน / ผู้รับสินค้า"), (Right - 110, "ผู้มีอำนาจลงนาม") })
        {
            pages[pageIndex].Add(new PdfStroke(center - 90, signY, center + 90, signY, 0.5));
            Write(pageIndex, label, center, signY + 4, align: 0.5);
            Write(pageIndex, "วันที่ ......../......../........", center, signY + 18, 9, align: 0.5, color: gray);
        }

        Write(pageIndex, "เอกสารนี้จัดทำในรูปแบบอิเล็กทรอนิกส์ (e-Tax Invoice)", PageWidth / 2, PageHeight - 24, 8, align: 0.5, color: gray);

        var drawn = PdfDrawing.Create(pages.Select(p => new PdfPageDrawing(PageWidth, PageHeight, p)).ToList(), $"{invoice.ThaiName} {invoice.Number}");
        var withText = PdfTextWriter.AddText(drawn, TextShaper.ToBlocks(text));
        var xml = invoice.ToXml(created);
        return PdfA.Convert(
            withText,
            new WpfPdfAFontSource(),
            $"{invoice.ThaiName} {invoice.Number}",
            created,
            [new PdfAAttachment(XmlFileName, xml, "text/xml", "Source", "e-Tax Invoice (ETDA ขมธอ. 3-2560)")]).Pdf;
    }

    /// <summary>The address as typed, with the postcode at the end of its last line (unless it is written there already).</summary>
    private static List<string> AddressLines(TaxParty party)
    {
        var lines = party.Address.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var postcode = party.Postcode.Trim();
        if (postcode.Length > 0 && !party.Address.Contains(postcode, StringComparison.Ordinal))
        {
            if (lines.Count == 0)
            {
                lines.Add(postcode);
            }
            else
            {
                lines[^1] += " " + postcode;
            }
        }

        return lines;
    }

    private static string Number(decimal value, int decimals = 2) => value.ToString(decimals == 0 ? "#,##0" : "#,##0.00", CultureInfo.InvariantCulture);

    private static string FormatId(string id)
    {
        var d = new string(id.Where(char.IsAsciiDigit).ToArray());
        return d.Length == 13 ? $"{d[0]}-{d[1..5]}-{d[5..10]}-{d[10..12]}-{d[12]}" : id.Trim();
    }

    private static string Branch(string branch)
    {
        var d = new string(branch.Where(char.IsAsciiDigit).ToArray()).PadLeft(5, '0');
        return d == "00000" ? "สำนักงานใหญ่" : $"สาขาที่ {d}";
    }

    private static CultureInfo CreateThaiCulture()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("th-TH").Clone();
        culture.DateTimeFormat.Calendar = new ThaiBuddhistCalendar();
        return culture;
    }
}
