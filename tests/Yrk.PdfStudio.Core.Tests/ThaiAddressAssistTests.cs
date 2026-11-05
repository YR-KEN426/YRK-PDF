using Yrk.PdfStudio.Core.Pdf.ETax;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ThaiAddressAssistTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Fills_a_single_postcode_when_empty(string? currentText)
    {
        var suggestion = ThaiAddressAssist.SuggestPostcode("100101", currentText, null);
        Assert.Equal("10200", suggestion.TextToSet);
        Assert.Single(suggestion.Options);
        Assert.Equal(ThaiAddressNotice.None, suggestion.Notice);
    }

    [Fact]
    public void Suggests_the_primary_and_returns_multiple_options_and_a_message_key()
    {
        var suggestion = ThaiAddressAssist.SuggestPostcode("100508", "", null);
        Assert.Equal("10220", suggestion.TextToSet);
        Assert.Equal(new[] { "10220", "10230" }, suggestion.Options.Select(option => option.Postcode));
        Assert.Equal(ThaiAddressNotice.MultiplePostcodes, suggestion.Notice);
    }

    [Theory]
    [InlineData("10550", ThaiAddressNotice.PostcodeMismatch)]
    [InlineData("99999", ThaiAddressNotice.PostcodeNotFound)]
    [InlineData("12", ThaiAddressNotice.None)]
    [InlineData("custom", ThaiAddressNotice.None)]
    public void Preserves_manually_typed_text(string currentText, ThaiAddressNotice notice)
    {
        var suggestion = ThaiAddressAssist.SuggestPostcode("100101", currentText, "10220");
        Assert.Null(suggestion.TextToSet);
        Assert.Equal(notice, suggestion.Notice);
        Assert.Single(suggestion.Options);
    }

    [Fact]
    public void Replaces_the_last_automatic_value_when_the_subdistrict_changes()
    {
        var first = ThaiAddressAssist.SuggestPostcode("100101", "", null);
        var second = ThaiAddressAssist.SuggestPostcode("100508", first.TextToSet, first.TextToSet);
        Assert.Equal("10220", second.TextToSet);
    }

    [Fact]
    public void Keeps_a_manually_selected_alternative_postcode()
    {
        var suggestion = ThaiAddressAssist.SuggestPostcode("100508", "10230", "10220");
        Assert.Null(suggestion.TextToSet);
        Assert.Equal(ThaiAddressNotice.MultiplePostcodes, suggestion.Notice);
        Assert.Equal(ThaiPostcodeMatch.Match, ThaiAddressAssist.Check("100508", "10230"));
    }

    [Fact]
    public void Does_not_guess_when_the_subdistrict_has_no_data()
    {
        var suggestion = ThaiAddressAssist.SuggestPostcode("104701", "", "10200");
        Assert.Null(suggestion.TextToSet);
        Assert.Empty(suggestion.Options);
        Assert.Equal(ThaiPostcodeMatch.Unknown, ThaiAddressAssist.Check("104701", "10200"));
    }

    [Fact]
    public void A_postcode_with_one_subdistrict_identifies_every_level()
    {
        var result = ThaiAddressAssist.Narrow("10550");
        Assert.True(result.IsFiltered);
        Assert.Equal("11", result.UniqueProvince?.Code);
        Assert.Equal("1102", result.UniqueDistrict?.Code);
        Assert.Equal("110205", result.UniqueSubdistrict?.Code);
        Assert.Single(result.Provinces);
        Assert.Single(result.Districts);
        Assert.Single(result.Subdistricts);
    }

    [Fact]
    public void A_shared_postcode_leaves_the_subdistrict_for_the_user_to_choose()
    {
        var result = ThaiAddressAssist.Narrow("10200");
        Assert.True(result.IsFiltered);
        Assert.Equal("10", result.UniqueProvince?.Code);
        Assert.Equal("1001", result.UniqueDistrict?.Code);
        Assert.Null(result.UniqueSubdistrict);
        Assert.True(result.Subdistricts.Count > 1);
        Assert.Equal(ThaiPostcodes.SubdistrictsWith("10200").Order(), result.Subdistricts.Select(area => area.Code).Order());
    }

    [Fact]
    public void A_postcode_crossing_districts_keeps_all_matching_paths()
    {
        var result = ThaiAddressAssist.Narrow("10220");
        Assert.True(result.IsFiltered);
        Assert.True(result.Districts.Count > 1);
        Assert.Null(result.UniqueDistrict);
        Assert.Null(result.UniqueSubdistrict);
        Assert.Contains(result.Subdistricts, area => area.Code == "100508");
        Assert.All(result.Subdistricts, area =>
        {
            Assert.Contains(result.Districts, district => district.Code == area.Code[..4]);
            Assert.Contains(result.Provinces, province => province.Code == area.Code[..2]);
            Assert.Equal(ThaiPostcodeMatch.Match, ThaiAddressAssist.Check(area.Code, "10220"));
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1022")]
    [InlineData("102200")]
    [InlineData(" 10220")]
    [InlineData("10220 ")]
    [InlineData("๑๐๒๒๐")]
    [InlineData("10x20")]
    public void Incomplete_or_invalid_postcodes_restore_full_lists(string? postcode)
    {
        Assert.True(ThaiAddressAssist.Narrow("10550").IsFiltered);
        var result = ThaiAddressAssist.Narrow(postcode);
        Assert.False(result.IsFiltered);
        Assert.Empty(result.Provinces);
        Assert.Empty(result.Districts);
        Assert.Empty(result.Subdistricts);
        Assert.Null(result.UniqueProvince);
        Assert.Null(result.UniqueDistrict);
        Assert.Null(result.UniqueSubdistrict);
        Assert.Equal(ThaiAddressNotice.None, result.Notice);
    }

    [Fact]
    public void Unknown_postcodes_do_not_filter_and_return_a_message_key()
    {
        var result = ThaiAddressAssist.Narrow("99999");
        Assert.False(result.IsFiltered);
        Assert.Empty(result.Provinces);
        Assert.Empty(result.Districts);
        Assert.Empty(result.Subdistricts);
        Assert.Equal(ThaiAddressNotice.PostcodeNotFound, result.Notice);
    }

    [Theory]
    [InlineData("100101", "10200", ThaiPostcodeMatch.Match)]
    [InlineData("100508", "10220", ThaiPostcodeMatch.Match)]
    [InlineData("100508", "10230", ThaiPostcodeMatch.Match)]
    [InlineData("100101", "10550", ThaiPostcodeMatch.Mismatch)]
    [InlineData("100101", "99999", ThaiPostcodeMatch.Unknown)]
    [InlineData("100101", "1020", ThaiPostcodeMatch.Unknown)]
    [InlineData("104701", "10200", ThaiPostcodeMatch.Unknown)]
    [InlineData(null, "10200", ThaiPostcodeMatch.Unknown)]
    [InlineData("100101", null, ThaiPostcodeMatch.Unknown)]
    public void Checks_match_mismatch_and_missing_data(string? subdistrict, string? postcode, ThaiPostcodeMatch expected) =>
        Assert.Equal(expected, ThaiAddressAssist.Check(subdistrict, postcode));
}
