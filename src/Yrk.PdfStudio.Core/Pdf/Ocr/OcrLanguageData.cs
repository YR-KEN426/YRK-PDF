using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace Yrk.PdfStudio.Core.Pdf.Ocr;

/// <summary>
/// Tesseract language files (tessdata_fast, Apache-2.0). They are not shipped with the app; each is
/// downloaded once, when the user first runs OCR in that language.
/// </summary>
public sealed class OcrLanguageData
{
    private const string DownloadBase = "https://github.com/tesseract-ocr/tessdata_fast/raw/87416418657359cb625c412a48b6e1d6d41c29bd/";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly HttpClient _client;
    private readonly IReadOnlyDictionary<string, (string Sha256, long Size)> _verificationData;

    // To refresh: select a new tessdata_fast commit SHA, download every listed .traineddata
    // file at that SHA, compute sha256sum and byte sizes, then update DownloadBase's SHA
    // and this entire table together. Never use hashes from a different revision.
    internal static IReadOnlyDictionary<string, (string Sha256, long Size)> VerificationData { get; } =
        new ReadOnlyDictionary<string, (string Sha256, long Size)>(new Dictionary<string, (string Sha256, long Size)>
        {
            ["tha"] = ("294227cc2d1292b0acb28d61d4115c88252b96d466ca90b417cf4cf0c67bf07c", 1072600),
            ["eng"] = ("7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2", 4113088),
            ["chi_sim"] = ("a5fcb6f0db1e1d6d8522f39db4e848f05984669172e584e8d76b6b3141e1f730", 2469156),
            ["jpn"] = ("1f5de9236d2e85f5fdf4b3c500f2d4926f8d9449f28f5394472d9e8d83b91b4d", 2471260),
            ["kor"] = ("6b85e11d9bbf07863b97b3523b1b112844c43e713df8b66418a081fd1060b3b2", 1677415),
            ["lao"] = ("20124962e93e68121e02c49a949d1f9df5db87dd62e5e4aa578362ca532444f8", 6386744),
            ["khm"] = ("47f110575341b322052f3becbefee61a3ecf1ef549352b5d4d33d28afe30d099", 1446926),
            ["mya"] = ("02aa6c25cfe9e583fa7b5d4131eac948f962308983f0f397df077dea58212b03", 4640561),
            ["vie"] = ("79df64caf7bcfb2a27df5042ecb6121e196eada34da774956995747636d5bfa1", 531275),
            ["fra"] = ("ced037562e8c80c13122dece28dd477d399af80911a28791a66a63ac1e3445ca", 1130365),
            ["deu"] = ("19d219bbb6672c869d20a9636c6816a81eb9a71796cb93ebe0cb1530e2cdb22d", 1525436),
        });

    public OcrLanguageData(string? folder = null) : this(folder, Client, VerificationData)
    {
    }

    // The injected client is owned by the caller; the public constructor shares Client.
    internal OcrLanguageData(string? folder, HttpClient client,
        IReadOnlyDictionary<string, (string Sha256, long Size)> verificationData)
    {
        Folder = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "tessdata");
        _client = client;
        _verificationData = verificationData;
    }

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

    public string Folder { get; }

    public bool IsInstalled(string language) => File.Exists(PathOf(language));

    /// <summary>Downloads a language file (a few MB) into <see cref="Folder"/>, atomically.</summary>
    public async Task DownloadAsync(string language, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Languages.All(l => l.Code != language) || !_verificationData.TryGetValue(language, out var expected))
        {
            throw new ArgumentException($"Unknown OCR language or missing verification data for '{language}'.", nameof(language));
        }

        Directory.CreateDirectory(Folder);
        var target = PathOf(language);
        var temp = target + ".download";
        try
        {
            using var response = await _client.GetAsync(DownloadBase + language + ".traineddata", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[81920];
                int read;
                // Read at most one extra byte to detect an oversized response, without writing it.
                while ((read = await input.ReadAsync(buffer.AsMemory(0,
                    (int)Math.Min(buffer.Length, expected.Size - done + 1)), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (read > expected.Size - done)
                    {
                        throw new InvalidDataException($"OCR language '{language}' exceeds expected size of {expected.Size} bytes.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    if (expected.Size > 0)
                    {
                        progress?.Report((double)done / expected.Size);
                    }
                }
            }

            if (done != expected.Size)
            {
                throw new InvalidDataException($"OCR language '{language}' size mismatch: expected {expected.Size} bytes, received {done}.");
            }
            var actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actualHash, expected.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"OCR language '{language}' SHA-256 mismatch.");
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
