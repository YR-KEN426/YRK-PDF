namespace Yrk.PdfStudio.Core.Pdf.Ocr;

/// <summary>
/// Tesseract language files (tessdata_fast, Apache-2.0). They are not shipped with the app; each is
/// downloaded once, when the user first runs OCR in that language.
/// </summary>
public sealed class OcrLanguageData(string? folder = null)
{
    private const string DownloadBase = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static IReadOnlyList<(string Code, string Name)> Languages { get; } =
    [
        ("tha", "Thai (ไทย)"),
        ("eng", "English"),
        ("chi_sim", "Chinese, simplified"),
        ("jpn", "Japanese"),
        ("kor", "Korean"),
        ("lao", "Lao"),
        ("khm", "Khmer"),
        ("mya", "Burmese"),
        ("vie", "Vietnamese"),
        ("fra", "French"),
        ("deu", "German"),
    ];

    public string Folder { get; } = folder ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "tessdata");

    public bool IsInstalled(string language) => File.Exists(PathOf(language));

    /// <summary>Downloads a language file (a few MB) into <see cref="Folder"/>, atomically.</summary>
    public async Task DownloadAsync(string language, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Languages.All(l => l.Code != language))
        {
            throw new ArgumentException($"Unknown OCR language '{language}'.", nameof(language));
        }

        Directory.CreateDirectory(Folder);
        var target = PathOf(language);
        var temp = target + ".download";
        try
        {
            using var response = await Client.GetAsync(DownloadBase + language + ".traineddata", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                    {
                        progress?.Report((double)done / total.Value);
                    }
                }
            }

            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private string PathOf(string language) => Path.Combine(Folder, language + ".traineddata");
}
