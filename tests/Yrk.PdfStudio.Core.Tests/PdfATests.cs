using System.Text;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Archive;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class PdfATests : IDisposable
{
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-pdfa-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    private static PdfAResult Convert(byte[] pdf) =>
        Sta.Run(() => PdfA.Convert(pdf, new App.Imaging.WpfPdfAFontSource(), "Report", new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.FromHours(7))));

    [Fact]
    public async Task Standard_fonts_are_embedded_and_the_text_stays_the_same()
    {
        var source = TestPdfFactory.CreateReportPdf(Path.Combine(_folder.FullName, "report.pdf"));
        var result = Convert(File.ReadAllBytes(source));

        var document = CosDocument.Load(result.Pdf);
        var fonts = document.GetPages()[0].Dictionary;
        var resources = document.Resolve(fonts["Resources"]) as CosDictionary;
        var fontDictionary = document.Resolve(resources!["Font"]) as CosDictionary;
        foreach (var font in fontDictionary!.Entries.Values.Select(document.Resolve).OfType<CosDictionary>())
        {
            Assert.Equal("TrueType", (font["Subtype"] as CosName)?.Value);
            var descriptor = document.Resolve(font["FontDescriptor"]) as CosDictionary;
            Assert.NotNull(document.Resolve(descriptor!["FontFile2"]));
            Assert.Equal(224, (document.Resolve(font["Widths"]) as CosArray)!.Count);
        }

        Assert.Contains(result.Changes, c => c.Format == "Embedded an installed font for {0}" && c.Argument == "Helvetica-Bold");

        var path = Path.Combine(_folder.FullName, "report-a.pdf");
        File.WriteAllBytes(path, result.Pdf);
        await using var converted = await _engine.OpenAsync(path);
        var text = await converted.GetPageTextAsync(0);
        Assert.Contains("Monthly report", text, StringComparison.Ordinal);
        Assert.Contains("2,230.50", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_output_intent_and_header_follow_PDF_A_2b()
    {
        var source = TestPdfFactory.CreateReportPdf(Path.Combine(_folder.FullName, "report.pdf"));
        var result = Convert(File.ReadAllBytes(source));

        Assert.StartsWith("%PDF-1.7\n%", Encoding.Latin1.GetString(result.Pdf, 0, 10), StringComparison.Ordinal);
        var document = CosDocument.Load(result.Pdf);
        var catalog = document.Catalog;

        var intents = Assert.IsType<CosArray>(document.Resolve(catalog["OutputIntents"]));
        var intent = Assert.IsType<CosDictionary>(document.Resolve(Assert.Single(intents.Items)));
        Assert.Equal("GTS_PDFA1", (intent["S"] as CosName)?.Value);
        var profile = Assert.IsType<CosStream>(document.Resolve(intent["DestOutputProfile"]));
        var icc = CosFilters.Decode(profile, document.Resolve);
        Assert.Equal("acsp", Encoding.ASCII.GetString(icc, 36, 4));
        Assert.Equal(icc.Length, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(icc));

        var metadata = Assert.IsType<CosStream>(document.Resolve(catalog["Metadata"]));
        var xmp = Encoding.UTF8.GetString(metadata.Data.Span);
        Assert.Contains("<pdfaid:part>2</pdfaid:part>", xmp, StringComparison.Ordinal);
        Assert.Contains("<pdfaid:conformance>B</pdfaid:conformance>", xmp, StringComparison.Ordinal);
        Assert.Contains("<xmp:ModifyDate>2026-10-21T09:30:00+07:00</xmp:ModifyDate>", xmp, StringComparison.Ordinal);
        Assert.Contains(">Report<", xmp, StringComparison.Ordinal);

        var info = Assert.IsType<CosDictionary>(document.Resolve(document.Trailer["Info"]));
        Assert.Equal("D:20261021093000+07'00'", Encoding.ASCII.GetString(((CosString)info["ModDate"]!).Bytes));
        Assert.Equal("Report", ((CosString)info["Title"]!).ToText());
        Assert.IsType<CosArray>(document.Trailer["ID"]);
    }

    [Fact]
    public void A_drawing_with_layers_converts_and_keeps_its_layers()
    {
        var source = TestPdfFactory.CreateLayeredPdf(Path.Combine(_folder.FullName, "layers.pdf"));
        var result = Convert(File.ReadAllBytes(source));

        var document = CosDocument.Load(result.Pdf);
        var layers = Assert.IsType<CosDictionary>(document.Resolve(document.Catalog["OCProperties"]));
        var defaults = Assert.IsType<CosDictionary>(document.Resolve(layers["D"]));
        Assert.NotNull(defaults["Name"]); // Every configuration needs a name in PDF/A.
    }

    [Fact]
    public void A_font_that_is_not_installed_stops_the_conversion()
    {
        var pdf = Encoding.ASCII.GetBytes(File.ReadAllText(TestPdfFactory.CreateReportPdf(Path.Combine(_folder.FullName, "report.pdf")), Encoding.Latin1)
            .Replace("/BaseFont /Helvetica-Bold", "/BaseFont /NoSuchFontXyz", StringComparison.Ordinal));
        var path = Path.Combine(_folder.FullName, "missing.pdf");
        File.WriteAllBytes(path, PdfFiles.Compact(pdf));

        var error = Assert.IsType<PdfAException>(Assert.ThrowsAny<Exception>(() => Convert(File.ReadAllBytes(path))).InnerException);
        var problem = Assert.Single(error.Problems);
        Assert.Equal("NoSuchFontXyz", problem.Argument);
    }

    [Fact]
    public async Task Works_off_the_UI_thread_as_the_app_runs_it()
    {
        var source = File.ReadAllBytes(TestPdfFactory.CreateReportPdf(Path.Combine(_folder.FullName, "report.pdf")));
        var result = await Task.Run(() => PdfA.Convert(source, new App.Imaging.WpfPdfAFontSource()));
        Assert.Contains(result.Changes, c => c.Format == "Embedded an installed font for {0}");
    }

    [Fact]
    public void Glyph_names_cover_common_differences()
    {
        Assert.Equal('é', GlyphNames.ToChar("eacute"));
        Assert.Equal('Ü', GlyphNames.ToChar("Udieresis"));
        Assert.Equal('–', GlyphNames.ToChar("endash"));
        Assert.Equal('ก', GlyphNames.ToChar("uni0E01"));
        Assert.Equal('A', GlyphNames.ToChar("A"));
        Assert.Null(GlyphNames.ToChar("g123"));
    }
}
