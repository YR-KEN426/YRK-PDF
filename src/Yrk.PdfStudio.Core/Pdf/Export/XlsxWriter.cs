using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Yrk.PdfStudio.Core.Pdf.Export;

/// <summary>How a cell looks: border lines, background colour, bold text, text on several lines.</summary>
/// <remarks><see cref="NumberFormat"/> is a built-in Excel format id (set by the writer from how the number was written).</remarks>
public sealed record XlsxStyle(CellBorders Borders = CellBorders.None, PdfColor? Fill = null, bool Bold = false, bool Wrap = false, int NumberFormat = 0);

/// <summary>Cells joined into one, from its top-left cell (0-based).</summary>
public sealed record XlsxMerge(int Row, int Column, int RowSpan, int ColumnSpan);

/// <summary>
/// A worksheet: column widths in points and rows of cell texts (null = empty cell); optionally cell looks, merged
/// cells and row heights in points, by 0-based row (and column).
/// </summary>
public sealed record XlsxSheet(
    string Name,
    IReadOnlyList<double> ColumnWidths,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    IReadOnlyDictionary<(int Row, int Column), XlsxStyle>? Styles = null,
    IReadOnlyList<XlsxMerge>? Merges = null,
    IReadOnlyDictionary<int, double>? RowHeights = null);

/// <summary>
/// Writes a basic Excel workbook (.xlsx). Numbers such as "1,234.50" or "(500)" become real numbers so they
/// can be added up; everything else (codes with leading zeros, dates, IDs) stays text as written.
/// </summary>
public static partial class XlsxWriter
{
    private const string SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static void Write(Stream output, IReadOnlyList<XlsxSheet> sheets)
    {
        if (sheets.Count == 0)
        {
            sheets = [new XlsxSheet("Sheet1", [], [])];
        }

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var names = UniqueNames(sheets.Select(s => s.Name));
        var workbookSheets = new StringBuilder();
        var workbookRels = new StringBuilder();
        var overrides = new StringBuilder();
        var styles = sheets.SelectMany(AllStyles).Distinct().ToList();
        for (var i = 0; i < sheets.Count; i++)
        {
            var n = i + 1;
            workbookSheets.Append(CultureInfo.InvariantCulture, $"<sheet name=\"{OfficeXml.Escape(names[i])}\" sheetId=\"{n}\" r:id=\"rId{n}\"/>");
            workbookRels.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{n}\" Type=\"{RelNs}/worksheet\" Target=\"worksheets/sheet{n}.xml\"/>");
            overrides.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/xl/worksheets/sheet{n}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            OfficeXml.AddPart(zip, $"xl/worksheets/sheet{n}.xml", Sheet(sheets[i], styles));
        }

        OfficeXml.AddPart(zip, "[Content_Types].xml", OfficeXml.Header +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            overrides + "</Types>");
        OfficeXml.AddPart(zip, "_rels/.rels", OfficeXml.Header +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{RelNs}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        OfficeXml.AddPart(zip, "xl/workbook.xml", OfficeXml.Header +
            $"<workbook xmlns=\"{SheetNs}\" xmlns:r=\"{RelNs}\"><sheets>{workbookSheets}</sheets></workbook>");
        OfficeXml.AddPart(zip, "xl/_rels/workbook.xml.rels", OfficeXml.Header +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + workbookRels +
            $"<Relationship Id=\"rIdStyles\" Type=\"{RelNs}/styles\" Target=\"styles.xml\"/></Relationships>");
        OfficeXml.AddPart(zip, "xl/styles.xml", Styles(styles));
    }

    /// <summary>Reads "1,234.50", "-12", "(500)" and "12.5%" (as 0.125) as numbers; false for anything else.</summary>
    public static bool TryParseNumber(string text, out double value)
    {
        value = 0;
        var s = text.Trim();
        var negative = false;
        if (s.Length > 2 && s[0] == '(' && s[^1] == ')')
        {
            negative = true;
            s = s[1..^1].Trim();
        }

        var percent = s.EndsWith('%');
        if (percent)
        {
            s = s[..^1].TrimEnd();
        }

        if (!NumberPattern().IsMatch(s) || (s.TrimStart('-').Length > 1 && s.TrimStart('-')[0] == '0' && s.TrimStart('-')[1] != '.'))
        {
            return false; // "007" and "0812345678" are codes and phone numbers, not numbers.
        }

        if (!double.TryParse(s.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        value = (negative ? -value : value) / (percent ? 100 : 1);
        return true;
    }

    private static string Sheet(XlsxSheet sheet, List<XlsxStyle> styles)
    {
        var xml = new StringBuilder(OfficeXml.Header).Append(CultureInfo.InvariantCulture, $"<worksheet xmlns=\"{SheetNs}\">");
        if (sheet.ColumnWidths.Count > 0)
        {
            xml.Append("<cols>");
            for (var c = 0; c < sheet.ColumnWidths.Count; c++)
            {
                var width = Math.Clamp((sheet.ColumnWidths[c] / 5.5) + 2, 6, 80);
                xml.Append(CultureInfo.InvariantCulture, $"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{width:0.##}\" customWidth=\"1\"/>");
            }

            xml.Append("</cols>");
        }

        xml.Append("<sheetData>");
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var height = sheet.RowHeights is { } heights && heights.TryGetValue(r, out var h) ? FormattableString.Invariant($" ht=\"{Math.Clamp(h, 9, 409):0.##}\" customHeight=\"1\"") : string.Empty;
            xml.Append(CultureInfo.InvariantCulture, $"<row r=\"{r + 1}\"{height}>");

            // Cells with text, and empty ones that have borders or a colour.
            var styled = sheet.Styles?.Keys.Where(k => k.Row == r).Select(k => k.Column) ?? [];
            foreach (var c in Enumerable.Range(0, row.Count).Where(c => row[c] is { Length: > 0 }).Concat(styled).Distinct().Order())
            {
                var reference = ColumnName(c) + (r + 1).ToString(CultureInfo.InvariantCulture);
                var text = c < row.Count ? row[c] : null;
                var style = CellStyle(sheet, r, c, text) is { } look ? FormattableString.Invariant($" s=\"{styles.IndexOf(look) + 1}\"") : string.Empty;
                xml.Append(text is not { Length: > 0 } ? $"<c r=\"{reference}\"{style}/>"
                    : TryParseNumber(text, out var number) ? $"<c r=\"{reference}\"{style}><v>{number.ToString("R", CultureInfo.InvariantCulture)}</v></c>"
                    : $"<c r=\"{reference}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">{OfficeXml.Escape(text)}</t></is></c>");
            }

            xml.Append("</row>");
        }

        xml.Append("</sheetData>");
        if (sheet.Merges is { Count: > 0 } merges)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<mergeCells count=\"{merges.Count}\">");
            foreach (var m in merges)
            {
                xml.Append(CultureInfo.InvariantCulture,
                    $"<mergeCell ref=\"{ColumnName(m.Column)}{m.Row + 1}:{ColumnName(m.Column + m.ColumnSpan - 1)}{m.Row + m.RowSpan}\"/>");
            }

            xml.Append("</mergeCells>");
        }

        return xml.Append("</worksheet>").ToString();
    }

    /// <summary>A cell's look, with the number format that shows a number as it was written ("3.0%", "1,250.00").</summary>
    private static XlsxStyle? CellStyle(XlsxSheet sheet, int row, int column, string? text)
    {
        var look = sheet.Styles is { } all && all.TryGetValue((row, column), out var found) ? found : null;
        if (text is null || !TryParseNumber(text, out _))
        {
            return look;
        }

        var decimals = text.Contains('.');
        var format = text.Contains('%') ? (decimals ? 10 : 9) : text.Contains(',') ? (decimals ? 4 : 3) : 0;
        return format == 0 ? look : (look ?? new XlsxStyle()) with { NumberFormat = format };
    }

    private static IEnumerable<XlsxStyle> AllStyles(XlsxSheet sheet)
    {
        var cells = sheet.Rows.SelectMany((row, r) => row.Select((text, c) => (r, c, text)))
            .Concat((sheet.Styles?.Keys ?? []).Select(k => (k.Row, k.Column, (string?)null)));
        foreach (var (r, c, text) in cells)
        {
            var value = text ?? (r < sheet.Rows.Count && c < sheet.Rows[r].Count ? sheet.Rows[r][c] : null);
            if (CellStyle(sheet, r, c, value) is { } look)
            {
                yield return look;
            }
        }
    }

    /// <summary>The workbook's cell formats: the plain one, then one per look used (cell style index = list index + 1).</summary>
    private static string Styles(List<XlsxStyle> styles)
    {
        var fills = styles.Select(s => s.Fill).OfType<PdfColor>().Distinct().ToList();
        var borders = styles.Select(s => s.Borders).Distinct().Where(b => b != CellBorders.None).ToList();
        var xml = new StringBuilder(OfficeXml.Header).Append(CultureInfo.InvariantCulture, $"<styleSheet xmlns=\"{SheetNs}\">");
        xml.Append("<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Tahoma\"/></font><font><b/><sz val=\"11\"/><name val=\"Tahoma\"/></font></fonts>");
        xml.Append(CultureInfo.InvariantCulture, $"<fills count=\"{fills.Count + 2}\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>");
        foreach (var f in fills)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF{f.R:X2}{f.G:X2}{f.B:X2}\"/><bgColor indexed=\"64\"/></patternFill></fill>");
        }

        xml.Append(CultureInfo.InvariantCulture, $"</fills><borders count=\"{borders.Count + 1}\"><border><left/><right/><top/><bottom/><diagonal/></border>");
        foreach (var b in borders)
        {
            static string Side(string name, bool on) => on ? $"<{name} style=\"thin\"><color auto=\"1\"/></{name}>" : $"<{name}/>";
            xml.Append("<border>").Append(Side("left", b.HasFlag(CellBorders.Left))).Append(Side("right", b.HasFlag(CellBorders.Right)))
                .Append(Side("top", b.HasFlag(CellBorders.Top))).Append(Side("bottom", b.HasFlag(CellBorders.Bottom))).Append("<diagonal/></border>");
        }

        xml.Append("</borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
        xml.Append(CultureInfo.InvariantCulture, $"<cellXfs count=\"{styles.Count + 1}\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>");
        foreach (var s in styles)
        {
            var fill = s.Fill is { } color ? fills.IndexOf(color) + 2 : 0;
            var border = s.Borders == CellBorders.None ? 0 : borders.IndexOf(s.Borders) + 1;
            xml.Append(CultureInfo.InvariantCulture,
                $"<xf numFmtId=\"{s.NumberFormat}\" fontId=\"{(s.Bold ? 1 : 0)}\" fillId=\"{fill}\" borderId=\"{border}\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\">");
            xml.Append(s.Wrap ? "<alignment vertical=\"center\" wrapText=\"1\"/>" : "<alignment vertical=\"center\"/>").Append("</xf>");
        }

        return xml.Append("</cellXfs></styleSheet>").ToString();
    }

    /// <summary>0 → A, 25 → Z, 26 → AA.</summary>
    public static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + ((n - 1) % 26)) + name;
        }

        return name;
    }

    private static List<string> UniqueNames(IEnumerable<string> names)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in names)
        {
            var clean = new string(raw.Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray()).Trim('\'', ' ');
            clean = clean.Length == 0 ? "Sheet" : clean[..Math.Min(clean.Length, 31)];
            var name = clean;
            for (var n = 2; !used.Add(name); n++)
            {
                var suffix = $" ({n})";
                name = clean[..Math.Min(clean.Length, 31 - suffix.Length)] + suffix;
            }

            result.Add(name);
        }

        return result;
    }

    [GeneratedRegex(@"^-?(\d{1,3}(,\d{3})+|\d+)(\.\d+)?$")]
    private static partial Regex NumberPattern();
}
