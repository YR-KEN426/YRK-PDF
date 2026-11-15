using System.Text.Json;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ErrorCatalogTests
{
    [Fact]
    public void Codes_are_unique_and_match_the_stable_format()
    {
        Assert.NotEmpty(ErrorCatalog.All);
        Assert.Equal(ErrorCatalog.All.Count, ErrorCatalog.All.Select(error => error.Code).Distinct().Count());
        Assert.All(ErrorCatalog.All, error => Assert.Matches(@"^YRK-[A-Z]+-[0-9]{3}$", error.Code));
        Assert.All(ErrorCatalog.All, error => Assert.True(
            error.Code.StartsWith("YRK-APP-", StringComparison.Ordinal)
            || error.Code.StartsWith("YRK-SCN-", StringComparison.Ordinal)
            || error.Code.StartsWith("YRK-SIG-", StringComparison.Ordinal)
            || error.Code.StartsWith("YRK-SEC-", StringComparison.Ordinal)
            || error.Code.StartsWith("YRK-UPD-", StringComparison.Ordinal)
            || error.Code.StartsWith("YRK-OCR-", StringComparison.Ordinal)
            || error.Code.StartsWith("YRK-CNV-", StringComparison.Ordinal)));
    }

    [Fact]
    public void All_three_app_codes_exist()
    {
        Assert.Equal(new[] { "YRK-APP-001", "YRK-APP-002", "YRK-APP-003" },
            ErrorCatalog.All.Where(error => error.Code.StartsWith("YRK-APP-", StringComparison.Ordinal))
                .Select(error => error.Code).Order());
    }

    [Fact]
    public void Every_message_and_next_step_has_a_nonempty_Thai_translation()
    {
        using var stream = typeof(ErrorCatalog).Assembly.GetManifestResourceStream("Yrk.PdfStudio.App.Localization.th.json");
        Assert.NotNull(stream);
        using var translations = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        foreach (var error in ErrorCatalog.All)
        {
            foreach (var key in new[] { error.Message, error.NextStep })
            {
                Assert.False(string.IsNullOrWhiteSpace(key));
                Assert.True(translations.RootElement.TryGetProperty(key, out var translation), $"Missing Thai translation: {key}");
                Assert.False(string.IsNullOrWhiteSpace(translation.GetString()));
                Assert.Matches("[ก-๙]", translation.GetString()!);
            }
        }
    }

    [Fact]
    public void Every_scanner_kind_and_feeder_variant_has_its_own_catalogue_entry()
    {
        var errors = Enum.GetValues<ScannerErrorKind>().Select(kind => ErrorCatalog.ForScanner(kind)).ToList();
        errors.Add(ErrorCatalog.ForScanner(ScannerErrorKind.FeederEmpty, hasPages: true));

        Assert.Equal(9, errors.Count);
        Assert.Equal(errors.Count, errors.Select(error => error.Code).Distinct().Count());
        Assert.All(errors, error => Assert.Contains(error, ErrorCatalog.All));
        Assert.All(errors, error => Assert.StartsWith("YRK-SCN-", error.Code));
        Assert.All(typeof(ErrorCatalog).GetProperties().Where(p => p.PropertyType == typeof(ErrorInfo)), p => Assert.Contains((ErrorInfo)p.GetValue(null)!, ErrorCatalog.All));
        Assert.Equal(ErrorCatalog.ScannerOther, ErrorCatalog.ForScanner((ScannerErrorKind)999));
        Assert.Contains("Scanned pages have been kept.", ErrorCatalog.ScannerFeederEmptyWithPages.Message);
    }
}
