using System.IO;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>Rubber stamps: Thai dates, the words saved as text, and the user's own stamps.</summary>
public sealed class RubberStampTests
{
    private static readonly DateTime Day = new(2026, 10, 29);

    [Theory]
    [InlineData(StampDateStyle.ThaiShort, "29 ต.ค. 2569")]
    [InlineData(StampDateStyle.ThaiLong, "29 ตุลาคม 2569")]
    [InlineData(StampDateStyle.ThaiNumeric, "29/10/2569")]
    [InlineData(StampDateStyle.English, "29 Oct 2026")]
    [InlineData(StampDateStyle.None, null)]
    public void Shows_the_date_in_the_chosen_style(StampDateStyle style, string? expected)
    {
        Assert.Equal(expected, RubberStamp.FormatDate(Day, style));
    }

    [Theory]
    [InlineData(StampShape.Box)]
    [InlineData(StampShape.DoubleBox)]
    [InlineData(StampShape.Oval)]
    public void Keeps_the_words_as_text_inside_the_border(StampShape shape)
    {
        var design = new StampDesign("อนุมัติ", "บริษัท ABC จำกัด\nฝ่ายบัญชี", StampInk.Green, shape, StampDateStyle.ThaiShort);

        var stamp = Sta.Run(() => RubberStamp.Render(design, Day, pixelsPerPoint: 1));

        Assert.Equal(["อนุมัติ", "บริษัท ABC จำกัด", "ฝ่ายบัญชี", "29 ต.ค. 2569"], stamp.Lines!.Select(l => l.Text));
        Assert.True(stamp.Lines![0].Bold);
        Assert.NotNull(stamp.Frame);
        Assert.Equal(RubberStamp.ColorOf(StampInk.Green), stamp.Ink);
        Assert.All(stamp.Lines!, line =>
        {
            Assert.InRange(line.TopLeft.X, 2, stamp.Size.Width / 2);
            Assert.InRange(line.TopLeft.Y, 2, stamp.Size.Height - line.FontSize);
        });

        // Top to bottom, in order.
        var tops = stamp.Lines!.Select(l => l.TopLeft.Y).ToList();
        Assert.Equal(tops.Order(), tops);
    }

    [Fact]
    public void Leaves_out_the_date_when_none_is_chosen()
    {
        var design = new StampDesign("ต้นฉบับ", null, StampInk.Red, StampShape.Box, StampDateStyle.None);

        var stamp = Sta.Run(() => RubberStamp.Render(design, Day, pixelsPerPoint: 1));

        Assert.Equal(["ต้นฉบับ"], stamp.Lines!.Select(l => l.Text));
    }

    [Fact]
    public void Offers_ready_made_stamps_in_thai_english_or_both()
    {
        var thai = RubberStamp.Presets(StampLanguage.Thai)[0];
        var english = RubberStamp.Presets(StampLanguage.English)[0];
        var both = RubberStamp.Presets(StampLanguage.Both)[0];

        Assert.Equal(("อนุมัติ", StampDateStyle.ThaiShort), (thai.Text, thai.Date));
        Assert.Equal(("APPROVED", StampDateStyle.English), (english.Text, english.Date));
        Assert.Equal(("อนุมัติ", "APPROVED"), (both.Text, both.Detail));
        Assert.Equal([0, 0, 0], new[] { thai, english, both }.Select(RubberStamp.PresetIndex));
        Assert.Equal(-1, RubberStamp.PresetIndex(thai with { Detail = "บริษัท ABC จำกัด" }));
        Assert.Equal(
            RubberStamp.Presets(StampLanguage.Thai).Select(p => (p.Ink, p.Shape)),
            RubberStamp.Presets(StampLanguage.English).Select(p => (p.Ink, p.Shape)));
    }

    [Fact]
    public void Remembers_the_users_own_stamps()
    {
        var path = Path.Combine(Path.GetTempPath(), "yrk-stamps-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var company = new StampDesign("รับเงินแล้ว", "บริษัท ABC จำกัด", StampInk.Blue, StampShape.Oval, StampDateStyle.ThaiLong);
            var settings = new StampSettings { Last = company, Language = StampLanguage.Both };
            settings.MyStamps.Add(company);
            settings.Save(path);

            var loaded = StampSettings.Load(path);

            Assert.Equal([company], loaded.MyStamps);
            Assert.Equal(company, loaded.Last);
            Assert.Equal(StampLanguage.Both, loaded.Language);
            Assert.Contains("\"Oval\"", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
