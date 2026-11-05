using Yrk.PdfStudio.Core.Pdf.ETax;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ThaiPostcodesTests
{
    private static IEnumerable<ThaiArea> Subdistricts => ThaiAreas.Provinces
        .SelectMany(province => ThaiAreas.DistrictsOf(province.Code))
        .SelectMany(district => ThaiAreas.SubdistrictsOf(district.Code));

    [Fact]
    public void Loads_the_embedded_resource_with_both_languages()
    {
        Assert.Contains("Yrk.PdfStudio.Core.Pdf.ETax.thai-postcodes.txt", typeof(ThaiPostcodes).Assembly.GetManifestResourceNames());
        var postcode = Assert.Single(ThaiPostcodes.PostcodesOf("100101"));
        Assert.Equal("10200", postcode.Postcode);
        Assert.Equal("ทั้งตำบล", postcode.DetailTh);
        Assert.Equal(postcode.DetailTh, postcode.Detail);
        Assert.Equal("Whole subdistrict", postcode.DetailEn);
        Assert.True(postcode.IsPrimary);
    }

    [Fact]
    public void Lists_the_primary_postcode_first_and_preserves_area_details()
    {
        var options = ThaiPostcodes.PostcodesOf("100508");
        Assert.Equal(new[] { "10220", "10230" }, options.Select(option => option.Postcode));
        Assert.True(options[0].IsPrimary);
        Assert.False(options[1].IsPrimary);
        Assert.Equal("ม.8 - 10", options[1].DetailTh);
        Assert.Equal("Moo 8 - 10", options[1].DetailEn);
    }

    [Fact]
    public void Covers_at_least_7420_revenue_department_subdistricts()
    {
        var areas = Subdistricts.ToArray();
        var missing = areas.Where(area => ThaiPostcodes.PostcodesOf(area.Code).Count == 0).Select(area => area.Code).Order().ToArray();
        Assert.True(areas.Length - missing.Length >= 7420);
        Assert.Equal(new[] { "104701", "105001" }, missing);
        Assert.Empty(ThaiPostcodes.PostcodesOf("100910")); // Not in our Revenue Department list.
        Assert.Contains(areas, area => ThaiPostcodes.PostcodesOf(area.Code).Count > 1);
    }

    [Fact]
    public void All_postcodes_have_five_ascii_digits_one_primary_and_a_reverse_lookup()
    {
        foreach (var area in Subdistricts)
        {
            var options = ThaiPostcodes.PostcodesOf(area.Code);
            if (options.Count == 0) continue;
            Assert.True(options[0].IsPrimary);
            Assert.Single(options, option => option.IsPrimary);
            Assert.Equal(options.Count, options.Select(option => option.Postcode).Distinct().Count());
            foreach (var option in options)
            {
                Assert.Matches("^[0-9]{5}$", option.Postcode);
                Assert.DoesNotContain('\t', option.DetailTh);
                Assert.DoesNotContain('\t', option.DetailEn);
                Assert.Contains(area.Code, ThaiPostcodes.SubdistrictsWith(option.Postcode));
            }
        }
    }

    [Fact]
    public void Shared_postcodes_return_multiple_known_subdistricts()
    {
        var areas = ThaiPostcodes.SubdistrictsWith("10200");
        Assert.True(areas.Count > 1);
        Assert.Contains("100101", areas);
        Assert.Contains("100102", areas);
        Assert.All(areas, code => Assert.NotEmpty(ThaiAreas.Name(code)));
        Assert.Equal(areas.Count, areas.Distinct().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("000000")]
    public void Unknown_subdistricts_are_empty(string? code) => Assert.Empty(ThaiPostcodes.PostcodesOf(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1020")]
    [InlineData("99999")]
    public void Unknown_postcodes_are_empty(string? code) => Assert.Empty(ThaiPostcodes.SubdistrictsWith(code));

    [Fact]
    public void Skips_damaged_rows_and_keeps_primary_order_among_valid_rows()
    {
        using var reader = new StringReader(
            "# source\n\n10010\t10200\tbad\tbad\n100101\t1020\tbad\tbad\n" +
            "๑๐๐๑๐๑\t10200\tbad\tbad\n100101\t๑๐๒๐๐\tbad\tbad\n" +
            "100101\t10200\tbad\textra\ttab\n100101\t10200\tonly three\n" +
            "100101\t10200\tbroken\ncontinuation\n" +
            "100101\t10200\tไทย\tEnglish\n" +
            "100101\t10200\tduplicate\tduplicate\n" +
            "100101\t10230\tอื่น\tOther\n000001\t00001\t\t\n");

        var loaded = ThaiPostcodes.Load(reader);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(new[] { "10200", "10230" }, loaded["100101"].Select(option => option.Postcode));
        Assert.Equal("ไทย", loaded["100101"][0].DetailTh);
        Assert.Equal("English", loaded["100101"][0].DetailEn);
        Assert.True(loaded["100101"][0].IsPrimary);
        Assert.False(loaded["100101"][1].IsPrimary);
        Assert.Equal("00001", Assert.Single(loaded["000001"]).Postcode);
    }
}
