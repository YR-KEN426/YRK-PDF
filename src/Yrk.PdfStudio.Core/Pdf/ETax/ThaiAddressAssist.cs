namespace Yrk.PdfStudio.Core.Pdf.ETax;

/// <summary>Translation keys for the UI to supply localized messages.</summary>
public enum ThaiAddressNotice { None, MultiplePostcodes, PostcodeMismatch, PostcodeNotFound }

public enum ThaiPostcodeMatch { Match, Mismatch, Unknown }

/// <param name="TextToSet">Null means leave the current text unchanged.</param>
public sealed record ThaiPostcodeSuggestion(
    string? TextToSet, IReadOnlyList<ThaiPostcode> Options, ThaiAddressNotice Notice);

/// <summary>Empty lists with IsFiltered=false mean use the full area lists and preserve existing selections.</summary>
public sealed record ThaiAddressNarrowing(
    bool IsFiltered,
    IReadOnlyList<ThaiArea> Provinces,
    IReadOnlyList<ThaiArea> Districts,
    IReadOnlyList<ThaiArea> Subdistricts,
    ThaiAddressNotice Notice)
{
    public ThaiArea? UniqueProvince => Provinces.Count == 1 ? Provinces[0] : null;
    public ThaiArea? UniqueDistrict => Districts.Count == 1 ? Districts[0] : null;
    public ThaiArea? UniqueSubdistrict => Subdistricts.Count == 1 ? Subdistricts[0] : null;
}

public sealed record ThaiAddressSelection(string? Province, string? District, string? Subdistrict);

public sealed record ThaiAddressPostcodeChange(
    ThaiAddressSelection Selection, ThaiAddressSelection? AutoSelected, ThaiAddressNarrowing Narrowing);

/// <summary>Pure address suggestions and selection updates.</summary>
public static class ThaiAddressAssist
{
    /// <summary>Replace only the unchanged selection previously supplied by postcode narrowing.</summary>
    public static ThaiAddressPostcodeChange ChangePostcode(
        string? postcode, ThaiAddressSelection current, ThaiAddressSelection? autoSelected)
    {
        var selection = autoSelected is not null && current == autoSelected
            ? new ThaiAddressSelection(null, null, null) : current;
        if (selection.Province is not null)
            return new(selection, null, new(false, [], [], [], ThaiAddressNotice.None));

        var narrowing = Narrow(postcode);
        if (!narrowing.IsFiltered)
            return new(selection, null, narrowing);

        selection = new(narrowing.UniqueProvince?.Code, narrowing.UniqueDistrict?.Code,
            narrowing.UniqueSubdistrict?.Code);
        return new(selection, selection, narrowing);
    }

    public static ThaiPostcodeSuggestion SuggestPostcode(string? subdistrict, string? currentText, string? lastAutoFilled)
    {
        var options = ThaiPostcodes.PostcodesOf(subdistrict);
        var canReplace = string.IsNullOrWhiteSpace(currentText)
            || (lastAutoFilled is not null && string.Equals(currentText, lastAutoFilled, StringComparison.Ordinal));
        var textToSet = canReplace && options.Count > 0 ? options[0].Postcode : null;
        var notice = options.Count > 1 ? ThaiAddressNotice.MultiplePostcodes : ThaiAddressNotice.None;
        var effectiveText = textToSet ?? currentText;
        if (ThaiPostcodes.IsCode(effectiveText, 5))
        {
            notice = Check(subdistrict, effectiveText) switch
            {
                ThaiPostcodeMatch.Mismatch => ThaiAddressNotice.PostcodeMismatch,
                ThaiPostcodeMatch.Unknown => ThaiAddressNotice.PostcodeNotFound,
                _ => notice
            };
        }
        return new ThaiPostcodeSuggestion(textToSet, options, notice);
    }

    /// <summary>Only complete ASCII postcodes found in the data narrow lists. This never changes user selections.</summary>
    public static ThaiAddressNarrowing Narrow(string? postcode)
    {
        if (!ThaiPostcodes.IsCode(postcode, 5))
            return new(false, [], [], [], ThaiAddressNotice.None);
        var codes = ThaiPostcodes.SubdistrictsWith(postcode);
        if (codes.Count == 0)
            return new(false, [], [], [], ThaiAddressNotice.PostcodeNotFound);

        var provinceCodes = codes.Select(code => code[..2]).ToHashSet(StringComparer.Ordinal);
        var districtCodes = codes.Select(code => code[..4]).ToHashSet(StringComparer.Ordinal);
        var subdistrictCodes = codes.ToHashSet(StringComparer.Ordinal);
        var provinces = ThaiAreas.Provinces.Where(area => provinceCodes.Contains(area.Code)).ToArray();
        var districts = provinces.SelectMany(area => ThaiAreas.DistrictsOf(area.Code))
            .Where(area => districtCodes.Contains(area.Code)).ToArray();
        var subdistricts = districts.SelectMany(area => ThaiAreas.SubdistrictsOf(area.Code))
            .Where(area => subdistrictCodes.Contains(area.Code)).ToArray();
        return new(true, Array.AsReadOnly(provinces), Array.AsReadOnly(districts), Array.AsReadOnly(subdistricts), ThaiAddressNotice.None);
    }

    /// <summary>Unknown includes incomplete input or a subdistrict/postcode missing from the dataset.</summary>
    public static ThaiPostcodeMatch Check(string? subdistrict, string? postcode)
    {
        if (!ThaiPostcodes.IsCode(postcode, 5))
            return ThaiPostcodeMatch.Unknown;
        var options = ThaiPostcodes.PostcodesOf(subdistrict);
        if (options.Count == 0 || ThaiPostcodes.SubdistrictsWith(postcode).Count == 0)
            return ThaiPostcodeMatch.Unknown;
        return options.Any(option => option.Postcode == postcode) ? ThaiPostcodeMatch.Match : ThaiPostcodeMatch.Mismatch;
    }
}
