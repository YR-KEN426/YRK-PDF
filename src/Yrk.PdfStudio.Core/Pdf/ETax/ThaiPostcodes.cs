namespace Yrk.PdfStudio.Core.Pdf.ETax;

public sealed record ThaiPostcode(string Postcode, string DetailTh, string DetailEn, bool IsPrimary)
{
    /// <summary>Thai detail, for callers using the original Detail API.</summary>
    public string Detail => DetailTh;
}

/// <summary>Offline postal suggestions. The first valid row of each subdistrict is its primary postcode.</summary>
public static class ThaiPostcodes
{
    private sealed record Index(
        IReadOnlyDictionary<string, IReadOnlyList<ThaiPostcode>> BySubdistrict,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ByPostcode);

    private static readonly Lazy<Index> Data = new(() =>
    {
        using var stream = typeof(ThaiPostcodes).Assembly.GetManifestResourceStream("Yrk.PdfStudio.Core.Pdf.ETax.thai-postcodes.txt")
            ?? throw new InvalidOperationException("The list of Thai postal codes is missing.");
        using var reader = new StreamReader(stream);
        var bySubdistrict = Load(reader);
        var byPostcode = bySubdistrict
            .SelectMany(pair => pair.Value.Select(p => (p.Postcode, Subdistrict: pair.Key)))
            .GroupBy(pair => pair.Postcode)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<string>)Array.AsReadOnly(group.Select(p => p.Subdistrict).Order(StringComparer.Ordinal).ToArray()));
        return new Index(bySubdistrict, byPostcode);
    });

    public static IReadOnlyList<ThaiPostcode> PostcodesOf(string? subdistrictCode) =>
        subdistrictCode is not null && Data.Value.BySubdistrict.TryGetValue(subdistrictCode, out var codes) ? codes : [];

    public static IReadOnlyList<string> SubdistrictsWith(string? postcode) =>
        postcode is not null && Data.Value.ByPostcode.TryGetValue(postcode, out var codes) ? codes : [];

    internal static bool IsCode(string? value, int digits) =>
        value is not null && value.Length == digits && value.All(c => c is >= '0' and <= '9');

    // Kept separate from resource access so damaged rows can be checked without changing the shipped data.
    internal static IReadOnlyDictionary<string, IReadOnlyList<ThaiPostcode>> Load(TextReader reader)
    {
        var entries = new Dictionary<string, List<ThaiPostcode>>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#') || line.Split('\t') is not [var code, var postcode, var detailTh, var detailEn]
                || !IsCode(code, 6) || !IsCode(postcode, 5)
                || detailTh.IndexOfAny(['\r', '\n']) >= 0 || detailEn.IndexOfAny(['\r', '\n']) >= 0)
                continue;

            if (!entries.TryGetValue(code, out var options))
                entries[code] = options = [];
            if (options.Any(p => p.Postcode == postcode))
                continue;
            options.Add(new ThaiPostcode(postcode, detailTh, detailEn, options.Count == 0));
        }

        return entries.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ThaiPostcode>)pair.Value.AsReadOnly(), StringComparer.Ordinal);
    }
}
