using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanPaperSizeTests
{
    [Fact]
    public void Table_has_exact_order_and_dimensions()
    {
        var expected = new (string Id, double W, double H)[] {
            ("A3",297,420), ("A4",210,297), ("A5",148,210), ("A6",105,148),
            ("JisB4",257,364), ("JisB5",182,257), ("IsoB5",176,250),
            ("Letter",215.9,279.4), ("Legal",215.9,355.6), ("Executive",184.2,266.7),
            ("Tabloid",279.4,431.8), ("Statement",139.7,215.9), ("F4",210,330), ("Oficio",215.9,340),
            ("Photo4x6",101.6,152.4), ("Photo5x7",127,177.2), ("BusinessCard",88.9,50.8), ("IdCard",85.6,54) };
        Assert.Equal(expected, ScanPaperSizes.Standard.Select(p => (p.Id, p.WidthMm, p.HeightMm)));
        Assert.All(ScanPaperSizes.Standard, p => {
            Assert.True(p.WidthMm > 0 && p.HeightMm > 0);
            Assert.False(string.IsNullOrWhiteSpace(p.Group));
            // Cards use their specified landscape orientation.
            if (p.Group != "Card") Assert.True(p.HeightMm > p.WidthMm);
        });
    }

    [Fact]
    public void Filtering_preserves_unavailable_remembered_size_and_unknown_extent_shows_all()
    {
        var visible = ScanPaperSizes.Visible(new(215.9, 297), "Legal").ToArray();
        Assert.Contains(visible, p => p.Id == "A4");
        Assert.Contains(visible, p => p.Id == "Letter");
        Assert.Contains(visible, p => p.Id == "Legal" && !p.Fits(new(215.9, 297)));
        Assert.DoesNotContain(visible, p => p.Id == "A3");
        Assert.Equal(18, ScanPaperSizes.Visible(null, "Original").Count());
        var caps = new ScanCapabilities([], [], [], new(210, 297), new(216, 356));
        var legal = ScanPaperSizes.Standard.Single(p => p.Id == "Legal");
        Assert.False(legal.Fits(caps.Extent(ScanSource.Flatbed)));
        Assert.True(legal.Fits(caps.Extent(ScanSource.Duplex)));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(-1, 10)] [InlineData(double.NaN, 10)]
    [InlineData(double.PositiveInfinity, 10)] [InlineData(216, 400)]
    public void Custom_rejects_invalid_or_oversized_dimensions(double w, double h) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ScanPaperSizes.Custom(w, h, false, new(216, 356)));

    [Fact]
    public void Custom_converts_inches() => Assert.Equal(215.9, ScanPaperSizes.Custom(8.5, 11, true).WidthMm, 6);

    [Fact]
    public void Settings_round_trip_remembers_independent_preset_sizes_and_folder()
    {
        var settings = new AppSettings { ScanSaveFolder = "C:\\Scans" };
        settings.ScanPaperByPreset["Photo"] = new("Photo4x6");
        settings.ScanPaperByPreset["ColorDocument"] = new("Custom", 148, 210);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        Assert.Equal("C:\\Scans", restored.ScanSaveFolder);
        Assert.Equal("Photo4x6", restored.ScanPaperByPreset["Photo"].Size!.Id);
        Assert.Equal(148, restored.ScanPaperByPreset["ColorDocument"].Size!.WidthMm);
        Assert.False(restored.ScanPaperByPreset.ContainsKey("Receipt"));
        foreach (var size in ScanPaperSizes.Standard)
            Assert.Contains(size.Name, Yrk.PdfStudio.App.Localization.Loc.ThaiKeys);
    }

    [Theory]
    [InlineData("A5", 148, 210)] [InlineData("Legal", 215.9, 355.6)] [InlineData("IdCard", 85.6, 54)]
    public void Crop_uses_dimensions_and_independent_actual_dpi(string id, double w, double h)
    {
        var crop = ScanPaperSizes.Standard.Single(p => p.Id == id).Crop(4000, 6000, 300, 400);
        Assert.Equal(w / 25.4 * 300 / 4000, crop.Width, 8);
        Assert.Equal(h / 25.4 * 400 / 6000, crop.Height, 8);
        Assert.Equal(0, crop.X); Assert.Equal(0, crop.Y);
    }

    [Theory]
    [InlineData(true, true, "C:\\scan")]
    [InlineData(false, true, "C:\\docs")]
    [InlineData(false, false, "C:\\Documents")]
    public void Save_folder_falls_back_silently(bool rememberedExists, bool documentExists, string expected)
    {
        Assert.Equal(expected, ScanPreferences.SaveFolder("C:\\scan", "C:\\docs\\open.pdf", "C:\\Documents",
            p => p == "C:\\scan" ? rememberedExists : documentExists));
        Assert.Equal("C:\\Documents", ScanPreferences.SaveFolder(null, null, "C:\\Documents", _ => false));
    }
}
