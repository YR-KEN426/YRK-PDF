using Tesseract;

namespace Yrk.PdfStudio.Core.Pdf.Ocr;

/// <summary>
/// Text recognition with Tesseract 5 (Apache-2.0). Not thread-safe: use one instance per job.
/// </summary>
public sealed class TesseractOcrEngine : IDisposable
{
    private readonly TesseractEngine _engine;
    private readonly PageSegMode _segmentationMode;

    /// <exception cref="PdfException">The language data is missing or Tesseract could not start.</exception>
    public TesseractOcrEngine(OcrLanguageData data, IReadOnlyList<string> languages,
        OcrSegmentationMode segmentationMode = OcrSegmentationMode.Auto)
    {
        _segmentationMode = segmentationMode switch
        {
            OcrSegmentationMode.Auto => PageSegMode.Auto,
            OcrSegmentationMode.SingleColumn => PageSegMode.SingleColumn,
            _ => throw new ArgumentOutOfRangeException(nameof(segmentationMode)),
        };
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
        // Scan conversion (SingleColumn) lets Tesseract estimate the resolution: photos and forwarded images are
        // rendered at "300 dpi" but their text is far smaller, and the fixed hint lost whole phrases in testing
        // (2026-11-14). Real 300 dpi scans read the same either way. The legacy OCR path keeps the hint.
        using var pix = Pix.LoadFromMemory(GrayBmp(bitmap, resolutionHint: _segmentationMode == PageSegMode.Auto));
        using var page = _engine.Process(pix, _segmentationMode);
        using var iterator = page.GetIterator();
        var words = new List<OcrWord>();
        var lineIndex = 0;
        iterator.Begin();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = iterator.GetText(PageIteratorLevel.TextLine) ?? string.Empty;
            var lineConfidence = iterator.GetConfidence(PageIteratorLevel.TextLine);
            NormalizedRect? lineBounds = iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out var lineBox)
                ? new NormalizedRect((double)lineBox.X1 / bitmap.Width, (double)lineBox.Y1 / bitmap.Height,
                    (double)lineBox.Width / bitmap.Width, (double)lineBox.Height / bitmap.Height)
                : null;
            var fragments = new List<OcrWord>();
            do
            {
                var text = iterator.GetText(PageIteratorLevel.Word)?.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                var bounds = iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var box)
                    ? new NormalizedRect((double)box.X1 / bitmap.Width, (double)box.Y1 / bitmap.Height,
                        (double)box.Width / bitmap.Width, (double)box.Height / bitmap.Height)
                    : lineBounds ?? new NormalizedRect(0, 0, 0, 0);
                fragments.Add(new OcrWord(text, bounds,
                    iterator.GetConfidence(PageIteratorLevel.Word), false));
            }
            while (iterator.Next(PageIteratorLevel.TextLine, PageIteratorLevel.Word));
            words.AddRange(MergeLinePieces(fragments, line, lineIndex, lineConfidence, lineBounds));
            lineIndex++;
        }
        while (iterator.Next(PageIteratorLevel.TextLine));

        return words;
    }

    // Join Thai pieces up to real whitespace. Confidence is metadata for review, never a deletion rule.
    internal static IReadOnlyList<OcrWord> MergeLinePieces(IReadOnlyList<OcrWord> fragments, string line,
        int lineIndex = 0, float lineConfidence = 100, NormalizedRect? lineBounds = null)
    {
        var words = new List<OcrWord>();
        var cursor = 0;
        var run = new System.Text.StringBuilder();
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        float confidenceSum = 0;
        var pieces = 0;
        void Flush(bool spaceAfter)
        {
            if (run.Length > 0)
                words.Add(new OcrWord(run.ToString(), new NormalizedRect(left, top, right - left, bottom - top),
                    confidenceSum / pieces, spaceAfter, lineIndex, lineBounds?.Height ?? 0, lineConfidence, lineBounds));
            run.Clear();
            left = top = double.MaxValue;
            right = bottom = double.MinValue;
            confidenceSum = 0;
            pieces = 0;
        }
        foreach (var fragment in fragments)
        {
            var text = fragment.Text.Trim();
            if (text.Length == 0) continue;
            var at = line.IndexOf(text, cursor, StringComparison.Ordinal);
            var spaceAfter = at >= 0 && at + text.Length < line.Length
                && char.IsWhiteSpace(line[at + text.Length]) && !string.IsNullOrWhiteSpace(line[(at + text.Length)..]);
            if (at >= 0) cursor = at + text.Length;
            run.Append(text);
            left = Math.Min(left, fragment.Bounds.X);
            top = Math.Min(top, fragment.Bounds.Y);
            right = Math.Max(right, fragment.Bounds.X + fragment.Bounds.Width);
            bottom = Math.Max(bottom, fragment.Bounds.Y + fragment.Bounds.Height);
            confidenceSum += fragment.Confidence;
            pieces++;
            if (spaceAfter) Flush(true);
        }
        Flush(false);
        // TextLine is authoritative if the word iterator omits or disagrees with a piece. Use the
        // available line/union box rather than silently losing text or inventing individual word boxes.
        static string Compact(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (Compact(string.Concat(words.Select(w => w.Text))) != Compact(line))
        {
            if (string.IsNullOrWhiteSpace(line)) return [];
            var bounds = lineBounds ?? (words.Count == 0 ? new NormalizedRect(0, 0, 0, 0)
                : new NormalizedRect(words.Min(w => w.Bounds.X), words.Min(w => w.Bounds.Y),
                    words.Max(w => w.Bounds.X + w.Bounds.Width) - words.Min(w => w.Bounds.X),
                    words.Max(w => w.Bounds.Y + w.Bounds.Height) - words.Min(w => w.Bounds.Y)));
            return [new OcrWord(line.Trim(), bounds,
                fragments.Count == 0 ? lineConfidence : fragments.Average(w => w.Confidence), false,
                lineIndex, lineBounds?.Height ?? 0, lineConfidence, lineBounds)];
        }
        return words;
    }

    public void Dispose() => _engine.Dispose();

    /// <summary>An 8-bit greyscale BMP (Leptonica reads it directly; Tesseract works on grey anyway).</summary>
    internal static byte[] GrayBmp(PdfBitmap bitmap, bool resolutionHint = true)
    {
        var pixelsPerMetre = resolutionHint ? 11811 : 0; // 300 dpi, or 0 = unknown (Tesseract estimates it)
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
        BitConverter.TryWriteBytes(span[38..], pixelsPerMetre);
        BitConverter.TryWriteBytes(span[42..], pixelsPerMetre);
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
