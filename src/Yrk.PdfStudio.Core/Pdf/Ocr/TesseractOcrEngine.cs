using Tesseract;

namespace Yrk.PdfStudio.Core.Pdf.Ocr;

/// <summary>
/// Text recognition with Tesseract 5 (Apache-2.0). Not thread-safe: use one instance per job.
/// </summary>
public sealed class TesseractOcrEngine : IDisposable
{
    private const float MinConfidence = 15;

    private readonly TesseractEngine _engine;

    /// <exception cref="PdfException">The language data is missing or Tesseract could not start.</exception>
    public TesseractOcrEngine(OcrLanguageData data, IReadOnlyList<string> languages)
    {
        var missing = languages.Where(l => !data.IsInstalled(l)).ToList();
        if (languages.Count == 0 || missing.Count > 0)
        {
            throw new PdfException("OCR language data is missing: " + string.Join(", ", missing));
        }

        try
        {
            _engine = new TesseractEngine(data.Folder, string.Join("+", languages), EngineMode.LstmOnly);
            _engine.SetVariable("preserve_interword_spaces", "1");
        }
        catch (Exception ex) when (ex is TesseractException or DllNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            throw new PdfException("The OCR engine could not start: " + ex.Message, ex);
        }
    }

    /// <summary>Recognises the words on a rendered page (BGRA pixels, as displayed).</summary>
    public IReadOnlyList<OcrWord> Recognize(PdfBitmap bitmap, CancellationToken cancellationToken = default)
    {
        using var pix = Pix.LoadFromMemory(GrayBmp(bitmap));
        using var page = _engine.Process(pix, PageSegMode.Auto);
        using var iterator = page.GetIterator();
        var words = new List<OcrWord>();
        iterator.Begin();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = iterator.GetText(PageIteratorLevel.TextLine) ?? string.Empty;
            var cursor = 0;

            // Tesseract splits Thai into single characters (tone marks and upper vowels get boxes of their
            // own). Pieces are joined up to the next real space, so each run is placed as one string.
            var run = new System.Text.StringBuilder();
            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
            float confidenceSum = 0;
            var pieces = 0;

            void Flush(bool spaceAfter)
            {
                if (run.Length > 0 && confidenceSum / pieces >= MinConfidence)
                {
                    words.Add(new OcrWord(
                        run.ToString(),
                        new NormalizedRect(
                            (double)left / bitmap.Width,
                            (double)top / bitmap.Height,
                            (double)(right - left) / bitmap.Width,
                            (double)(bottom - top) / bitmap.Height),
                        confidenceSum / pieces,
                        spaceAfter));
                }

                run.Clear();
                left = top = int.MaxValue;
                right = bottom = int.MinValue;
                confidenceSum = 0;
                pieces = 0;
            }

            do
            {
                var text = iterator.GetText(PageIteratorLevel.Word)?.Trim();
                if (string.IsNullOrEmpty(text) || !iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var box))
                {
                    continue;
                }

                var at = line.IndexOf(text, cursor, StringComparison.Ordinal);
                var spaceAfter = at >= 0 && at + text.Length < line.Length && line[at + text.Length] == ' ';
                if (at >= 0)
                {
                    cursor = at + text.Length;
                }

                run.Append(text);
                left = Math.Min(left, box.X1);
                top = Math.Min(top, box.Y1);
                right = Math.Max(right, box.X2);
                bottom = Math.Max(bottom, box.Y2);
                confidenceSum += iterator.GetConfidence(PageIteratorLevel.Word);
                pieces++;
                if (spaceAfter)
                {
                    Flush(spaceAfter: true);
                }
            }
            while (iterator.Next(PageIteratorLevel.TextLine, PageIteratorLevel.Word));

            Flush(spaceAfter: false);
        }
        while (iterator.Next(PageIteratorLevel.TextLine));

        return words;
    }

    public void Dispose() => _engine.Dispose();

    /// <summary>An 8-bit greyscale BMP (Leptonica reads it directly; Tesseract works on grey anyway).</summary>
    internal static byte[] GrayBmp(PdfBitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        var rowLength = (width + 3) & ~3;
        const int paletteLength = 256 * 4;
        const int headerLength = 14 + 40 + paletteLength;
        var output = new byte[headerLength + (rowLength * height)];
        var span = output.AsSpan();

        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BitConverter.TryWriteBytes(span[2..], output.Length);
        BitConverter.TryWriteBytes(span[10..], headerLength);
        BitConverter.TryWriteBytes(span[14..], 40);
        BitConverter.TryWriteBytes(span[18..], width);
        BitConverter.TryWriteBytes(span[22..], height); // Positive: rows are stored bottom-up.
        BitConverter.TryWriteBytes(span[26..], (short)1);
        BitConverter.TryWriteBytes(span[28..], (short)8);
        BitConverter.TryWriteBytes(span[34..], rowLength * height);
        BitConverter.TryWriteBytes(span[38..], 11811); // 300 dpi in pixels per metre
        BitConverter.TryWriteBytes(span[42..], 11811);
        BitConverter.TryWriteBytes(span[46..], 256);
        for (var i = 0; i < 256; i++)
        {
            span[54 + (i * 4)] = span[55 + (i * 4)] = span[56 + (i * 4)] = (byte)i;
        }

        for (var y = 0; y < height; y++)
        {
            var source = bitmap.Pixels.AsSpan(y * bitmap.Stride, width * 4);
            var target = span.Slice(headerLength + ((height - 1 - y) * rowLength), width);
            for (var x = 0; x < width; x++)
            {
                target[x] = (byte)(((source[(x * 4) + 2] * 299) + (source[(x * 4) + 1] * 587) + (source[x * 4] * 114)) / 1000);
            }
        }

        return output;
    }
}
