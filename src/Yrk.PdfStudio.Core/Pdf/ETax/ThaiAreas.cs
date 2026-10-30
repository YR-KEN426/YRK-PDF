using System.Globalization;

namespace Yrk.PdfStudio.Core.Pdf.ETax;

/// <summary>A province, district or subdistrict and its code.</summary>
public sealed record ThaiArea(string Code, string Name);

/// <summary>
/// Thai provinces (2 digits), districts (4) and subdistricts (6), which the e-Tax XML gives as codes (TIS 1099 and
/// ISO 3166-2:TH). The list comes from the Revenue Department's XMLSchemaV2 code lists, without areas marked as no
/// longer in use; a district's code starts with its province's, a subdistrict's with its district's.
/// </summary>
public static class ThaiAreas
{
    private static readonly Lazy<IReadOnlyList<ThaiArea>> All = new(Load);
    private static readonly Lazy<Dictionary<string, string>> Names = new(() => All.Value.ToDictionary(a => a.Code, a => a.Name));

    public static IReadOnlyList<ThaiArea> Provinces => Within(string.Empty, 2);

    public static IReadOnlyList<ThaiArea> DistrictsOf(string province) => Within(province, 4);

    public static IReadOnlyList<ThaiArea> SubdistrictsOf(string district) => Within(district, 6);

    /// <summary>The name of a code; empty when it is not known.</summary>
    public static string Name(string? code) => code is not null && Names.Value.TryGetValue(code, out var name) ? name : string.Empty;

    private static List<ThaiArea> Within(string parent, int length)
    {
        var thai = StringComparer.Create(CultureInfo.GetCultureInfo("th-TH"), ignoreCase: false);
        return All.Value.Where(a => a.Code.Length == length && a.Code.StartsWith(parent, StringComparison.Ordinal)).OrderBy(a => a.Name, thai).ToList();
    }

    private static List<ThaiArea> Load()
    {
        using var stream = typeof(ThaiAreas).Assembly.GetManifestResourceStream("Yrk.PdfStudio.Core.Pdf.ETax.thai-areas.txt")
            ?? throw new InvalidOperationException("The list of Thai areas is missing.");
        using var reader = new StreamReader(stream);
        var areas = new List<ThaiArea>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Split('\t') is [var code, var name])
            {
                areas.Add(new ThaiArea(code, name));
            }
        }

        return areas;
    }
}
