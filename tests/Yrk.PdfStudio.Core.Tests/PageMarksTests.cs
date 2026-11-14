using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class PageMarksTests : IDisposable
{
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");
    private static readonly DateTime Today = new(2026, 10, 19);
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-marks-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public void Page_numbers_and_placeholders_are_filled_in()
    {
        var settings = new PageMarkSettings { FooterCenter = "หน้า {page} จาก {pages}", HeaderRight = "{file}", FirstNumber = 5 };
        var marks = Sta.Run(() => PageMarks.Layout(settings, 1, 3, 595, 842, @"C:\docs\Contract.pdf", Today));

        Assert.Contains(marks, m => m.Text == "หน้า 6 จาก 7");
        var file = Assert.Single(marks, m => m.Text == "Contract");
        Assert.True(file.Left + file.Line.Width <= 595 - (12 * 72 / 25.4) + 0.01); // Right-aligned inside the margin.
        Assert.True(file.Top < 50);                                                // Header at the top.
    }

    [Fact]
    public void A_fitted_watermark_stays_inside_the_page_and_is_centred()
    {
        var settings = new PageMarkSettings { Watermark = "CONFIDENTIAL", WatermarkAngle = 45 };
        var mark = Assert.Single(Sta.Run(() => PageMarks.Layout(settings, 0, 1, 595, 842, "a.pdf", Today)));

        var a = Math.PI / 4;
        double w = mark.Line.Width, h = mark.Line.Height;
        var centreX = mark.Left + (w / 2 * Math.Cos(a)) + (h / 2 * Math.Sin(a));
        var centreY = mark.Top - (w / 2 * Math.Sin(a)) + (h / 2 * Math.Cos(a));
        Assert.Equal(595 / 2.0, centreX, 3);
        Assert.Equal(842 / 2.0, centreY, 3);
        Assert.True((w * Math.Cos(a)) + (h * Math.Sin(a)) <= 595 * 0.8 + 0.01);
    }

    [Fact]
    public void Watermarks_default_to_in_front_of_page_content()
    {
        Assert.False(new PageMarkSettings().WatermarkBehind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(-30)]
    [InlineData(90)]
    public void Explicit_lines_are_trimmed_fitted_and_rotated_as_one_centred_stack(double angle)
    {
        var settings = new PageMarkSettings
        {
            Watermark = "  สำเนา {file}  \r\n \n  CONFIDENTIAL  \r  Page {page}  \n ",
            WatermarkAngle = angle,
            WatermarkColor = new PdfColor(30, 60, 90),
            WatermarkOpacity = 0.4,
            WatermarkBehind = true,
        };
        var marks = Sta.Run(() => PageMarks.Layout(settings, 0, 1, 595, 842, "Contract.pdf", Today));

        Assert.Equal(new[] { "สำเนา Contract", "CONFIDENTIAL", "Page 1" }, marks.Select(mark => mark.Text));
        var a = angle * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        var totalHeight = marks.Sum(mark => mark.Line.Height);
        var y = -totalHeight / 2;
        foreach (var mark in marks)
        {
            double dx = mark.Left - (595 / 2.0), dy = mark.Top - (842 / 2.0);
            Assert.Equal(-mark.Line.Width / 2, (dx * cos) - (dy * sin), 3);
            Assert.Equal(y, (dx * sin) + (dy * cos), 3);
            y += mark.Line.Height;
            foreach (var x in new[] { 0, mark.Line.Width })
            {
                foreach (var localY in new[] { 0, mark.Line.Height })
                {
                    Assert.InRange(mark.Left + (x * cos) + (localY * sin), 0, 595);
                    Assert.InRange(mark.Top - (x * sin) + (localY * cos), 0, 842);
                }
            }

            Assert.Equal(marks[0].FontSize, mark.FontSize);
            Assert.InRange(mark.FontSize, 0.01, 200);
            Assert.Equal(angle, mark.Angle);
            Assert.Equal(settings.WatermarkColor, mark.Color);
            Assert.Equal(settings.WatermarkOpacity, mark.Opacity);
            Assert.True(mark.Behind);
            Assert.True(mark.Bold);
        }

        var w = marks.Max(mark => mark.Line.Width);
        Assert.True((w * Math.Abs(cos)) + (totalHeight * Math.Abs(sin)) <= 595 * 0.8 + 0.01);
        Assert.True((w * Math.Abs(sin)) + (totalHeight * Math.Abs(cos)) <= 842 * 0.8 + 0.01);
    }

    [Fact]
    public void Long_mixed_text_wraps_only_at_spaces_and_uses_a_larger_font()
    {
        const string text = "เอกสารสำคัญ CONFIDENTIAL สำหรับใช้ภายใน INTERNAL USE ONLY ห้ามเผยแพร่ WITHOUT WRITTEN PERMISSION";
        var settings = new PageMarkSettings { Watermark = text };
        var marks = Sta.Run(() => PageMarks.Layout(settings, 0, 1, 595, 842, "a.pdf", Today));
        var unit = Assert.Single(Sta.Run(() => PageMarks.Layout(settings with { WatermarkSize = 100 }, 0, 1, 595, 842, "a.pdf", Today)));
        var turnedExtent = (unit.Line.Width + unit.Line.Height) / Math.Sqrt(2);
        var singleSize = Math.Min(200, 100 * Math.Min(595 * 0.8 / turnedExtent, 842 * 0.8 / turnedExtent));

        Assert.InRange(marks.Count, 2, 4);
        Assert.Equal(text, string.Join(" ", marks.Select(mark => mark.Text)));
        Assert.All(marks, mark => Assert.True(mark.FontSize >= singleSize * 1.1));
    }

    [Theory]
    [InlineData("เอกสารสำคัญห้ามเผยแพร่สำหรับใช้ภายในเท่านั้นเอกสารสำคัญห้ามเผยแพร่สำหรับใช้ภายในเท่านั้น")]
    [InlineData("COPY")]
    [InlineData("สำเนา")]
    public void Text_without_spaces_or_short_text_stays_on_one_line(string text)
    {
        var settings = new PageMarkSettings { Watermark = text };
        var mark = Assert.Single(Sta.Run(() => PageMarks.Layout(settings, 0, 1, 595, 842, "a.pdf", Today)));

        Assert.Equal(text, mark.Text);
    }

    [Fact]
    public void Wrapping_is_rejected_when_the_font_improves_by_less_than_ten_percent()
    {
        var settings = new PageMarkSettings { Watermark = "WWW WWW", WatermarkAngle = 0 };
        var unit = Assert.Single(Sta.Run(() => PageMarks.Layout(settings with { WatermarkSize = 100 }, 0, 1, 595, 2000, "a.pdf", Today)));
        var width = unit.Line.Width * 1.9 / 0.8; // Single line fits at 190 pt; wrapping can only reach 200 pt.
        var mark = Assert.Single(Sta.Run(() => PageMarks.Layout(settings, 0, 1, width, 2000, "a.pdf", Today)));

        Assert.Equal(settings.Watermark, mark.Text);
        Assert.Equal(190, mark.FontSize, 3);
    }

    [Fact]
    public void Wrapping_balances_shaped_widths_and_prefers_fewer_lines_on_a_tie()
    {
        var settings = new PageMarkSettings { Watermark = "WWWWWW i WWWWW", WatermarkAngle = 0 };
        var marks = Sta.Run(() => PageMarks.Layout(settings, 0, 1, 200, 10000, "a.pdf", Today));

        // The narrow i belongs with the shorter W word; splitting into three cannot reduce the widest line.
        Assert.Equal(new[] { "WWWWWW", "i WWWWW" }, marks.Select(mark => mark.Text));
        Assert.True(marks[1].Line.Width <= marks[0].Line.Width);
    }

    [Fact]
    public void Equal_fitted_sizes_prefer_fewer_lines()
    {
        var settings = new PageMarkSettings { Watermark = "COPY COPY", WatermarkAngle = 0 };
        var mark = Assert.Single(Sta.Run(() => PageMarks.Layout(settings, 0, 1, 2000, 2000, "a.pdf", Today)));

        Assert.Equal(settings.Watermark, mark.Text);
        Assert.Equal(200, mark.FontSize);
    }

    [Theory]
    [InlineData("Long watermark text with spaces that must stay together", 1)]
    [InlineData("Long watermark text\nSecond explicit line", 2)]
    public void Manual_size_preserves_explicit_lines_without_automatic_wrapping(string text, int lineCount)
    {
        var settings = new PageMarkSettings { Watermark = text, WatermarkSize = 24 };
        var marks = Sta.Run(() => PageMarks.Layout(settings, 0, 1, 595, 842, "a.pdf", Today));

        Assert.Equal(lineCount, marks.Count);
        Assert.Equal(text.Split('\n'), marks.Select(mark => mark.Text));
        Assert.All(marks, mark => Assert.Equal(24, mark.FontSize));
    }

    [Fact]
    public void Blank_lines_and_empty_placeholders_produce_no_watermark()
    {
        var settings = new PageMarkSettings { Watermark = " \r\n {file} \r \n\t", HeaderLeft = "Header" };
        var mark = Assert.Single(Sta.Run(() => PageMarks.Layout(settings, 0, 1, 595, 842, ".pdf", Today)));

        Assert.Equal("Header", mark.Text);
        Assert.False(mark.Bold);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void Single_line_layout_matches_the_original_geometry(double size)
    {
        var settings = new PageMarkSettings { Watermark = "CONFIDENTIAL", WatermarkAngle = 45, WatermarkSize = size };
        var mark = Assert.Single(Sta.Run(() => PageMarks.Layout(settings, 0, 1, 595, 842, "a.pdf", Today)));
        var unit = Sta.Run(() => TextShaper.Shape(settings.Watermark, settings.FontFamily, true, false, 100));
        var a = Math.PI / 4;
        var extent = (unit.Width * Math.Cos(a)) + (unit.Height * Math.Sin(a));
        var expectedSize = size > 0 ? size : Math.Min(200, 100 * Math.Min(595 * 0.8 / extent, 842 * 0.8 / extent));

        Assert.Equal(expectedSize, mark.FontSize, 6);
        Assert.Equal((595 / 2.0) - ((mark.Line.Width / 2 * Math.Cos(a)) + (mark.Line.Height / 2 * Math.Sin(a))), mark.Left, 6);
        Assert.Equal((842 / 2.0) - ((-mark.Line.Width / 2 * Math.Sin(a)) + (mark.Line.Height / 2 * Math.Cos(a))), mark.Top, 6);
    }

    [Fact]
    public async Task Marks_are_written_as_text_with_a_see_through_watermark()
    {
        var settings = new PageMarkSettings
        {
            Watermark = "DRAFT",
            WatermarkAngle = 0,
            WatermarkColor = new PdfColor(220, 0, 0),
            WatermarkOpacity = 0.25,
            FooterCenter = "Page {page} of {pages}",
            SkipFirstPage = true,
        };
        var input = await File.ReadAllBytesAsync(SamplePath);
        IReadOnlyList<PdfPageSize> sizes;
        await using (var original = await _engine.OpenAsync(SamplePath))
        {
            sizes = original.PageSizes;
        }

        var blocks = Sta.Run(() => PageMarks.ToBlocks(settings, sizes, [0, 1, 2], "sample.pdf", Today));
        var path = Path.Combine(_folder.FullName, "marked.pdf");
        await File.WriteAllBytesAsync(path, PdfTextWriter.AddText(input, blocks));

        await using var document = await _engine.OpenAsync(path);
        Assert.DoesNotContain("Page 1 of 3", await document.GetPageTextAsync(0)); // First page skipped.
        var second = await document.GetPageTextAsync(1);
        Assert.Contains("Page 2 of 3", second);
        Assert.Contains("DRAFT", second);

        // The watermark is light red, not solid: 25 % red over white paper.
        var size = document.PageSizes[1];
        var bitmap = await document.RenderPageAsync(1, (int)size.Width, (int)size.Height);
        var solid = 0;
        var light = 0;
        for (var i = 0; i < bitmap.Pixels.Length; i += 4)
        {
            byte b = bitmap.Pixels[i], g = bitmap.Pixels[i + 1], r = bitmap.Pixels[i + 2];
            if (r > 200 && g < 60 && b < 60)
            {
                solid++;
            }
            else if (r > 230 && g is > 150 and < 225 && b is > 150 and < 225)
            {
                light++;
            }
        }

        Assert.Equal(0, solid);
        Assert.True(light > 200, $"Expected see-through red pixels, found {light}.");
    }
}
