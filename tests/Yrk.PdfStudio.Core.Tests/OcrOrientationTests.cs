using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Ocr;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class OcrOrientationTests : IDisposable
{
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-ocr-orientation-");
    private readonly OcrLanguageData _data = new();

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [InstalledOcrOrientationTheory]
    [InlineData(0, 0)]
    [InlineData(90, 0)]
    [InlineData(180, 0)]
    [InlineData(270, 0)]
    [InlineData(180, 90)]
    public async Task Corrects_display_orientation_and_places_search_text_on_the_upright_image(int rotation, int existingRotation)
    {
        var path = await CreateScanAsync((rotation - existingRotation + 360) % 360);
        var pdf = File.ReadAllBytes(path);
        if (existingRotation != 0)
        {
            // Use inherited /Rotate as well as a nonzero CropBox origin to exercise displayed coordinates.
            var source = CosDocument.Load(pdf);
            var page = source.GetPages()[0];
            var parentRef = Assert.IsType<CosReference>(page.Dictionary["Parent"]);
            var parent = Assert.IsType<CosDictionary>(source.Resolve(parentRef)).Clone();
            parent["Rotate"] = new CosInteger(existingRotation);
            var dictionary = page.Dictionary.Clone();
            dictionary["Rotate"] = null;
            var (x0, y0, x1, y1) = page.CropBox;
            dictionary["CropBox"] = CosArray.FromNumbers(x0 + 3, y0 + 3, x1 - 3, y1 - 3);
            var update = new CosIncrementalUpdate(source);
            update.Replace(parentRef.Id, parent);
            update.Replace(page.Id, dictionary);
            pdf = update.Build().Bytes;
            File.WriteAllBytes(path, pdf);
        }

        await using var document = await _engine.OpenAsync(path);
        var beforeSize = document.PageSizes[0];
        var result = await PdfOcr.RecognizeOrientedPagesAsync(document, pdf, new(["eng"]), _data);
        var words = Assert.Single(result.Pages).Words;
        Assert.NotEmpty(words);
        Assert.Contains(words, w => w.Text.Contains("quick", StringComparison.Ordinal));
        Assert.All(words, w =>
        {
            Assert.True(w.Line >= 0);
            Assert.True(w.LineHeight > 0);
            Assert.NotNull(w.LineBounds);
            Assert.InRange(w.LineConfidence, 0, 100);
        });
        var expectedRotation = (existingRotation + 360 - rotation) % 360;
        Assert.Equal(expectedRotation, CosDocument.Load(result.Pdf).GetPages()[0].Rotate);
        Assert.Equal(existingRotation, CosDocument.Load(pdf).GetPages()[0].Rotate);
        Assert.Equal(beforeSize, document.PageSizes[0]); // Input document is unchanged.
        if (rotation == 0) Assert.Same(pdf, result.Pdf);

        var output = Path.Combine(_folder.FullName, "upright.pdf");
        File.WriteAllBytes(output, PdfOcr.AddTextLayer(result.Pdf, result.Pages));
        await using var searchable = await _engine.OpenAsync(output);
        Assert.True(searchable.PageSizes[0].Height > searchable.PageSizes[0].Width);
        var matches = await searchable.FindTextAsync(0, "quick", PdfSearchOptions.None);
        Assert.NotEmpty(matches);
        var first = matches.MinBy(m => m.Rects[0].Y)!;
        Assert.InRange(first.Rects[0].X, 0.05, 0.20);
        Assert.InRange(first.Rects[0].Y, 0.03, 0.10);
        var ocrWord = words.First(w => w.Text.Contains("quick", StringComparison.Ordinal));
        Assert.InRange(Math.Abs(first.Rects[0].X - ocrWord.Bounds.X), 0, 0.02);
        Assert.InRange(Math.Abs(first.Rects[0].Y - ocrWord.Bounds.Y), 0, 0.02);

        // Compare actual rendered ink beneath the search box: /Rotate and text must agree visually.
        var bitmap = await searchable.RenderPageAsync(0, 620, 877);
        var box = first.Rects[0];
        var dark = 0;
        for (var y = (int)(box.Y * bitmap.Height); y < (box.Y + box.Height) * bitmap.Height; y++)
            for (var x = (int)(box.X * bitmap.Width); x < (box.X + box.Width) * bitmap.Width; x++)
                if (bitmap.Pixels[y * bitmap.Stride + x * 4] < 128) dark++;
        Assert.True(dark > 10, $"Only {dark} dark pixels under the search box.");
    }

    [InstalledEnglishOcrFact]
    public async Task Legacy_recognition_matches_raw_pages_and_does_not_correct_orientation()
    {
        var path = await CreateScanAsync(180);
        var pdf = File.ReadAllBytes(path);
        await using var document = await _engine.OpenAsync(path);
        var options = new OcrOptions(["eng"]);
        var pages = await PdfOcr.RecognizePagesAsync(document, options, _data);
        var expected = PdfOcr.AddTextLayer(pdf, pages);
        var summary = await PdfOcr.RecognizeAsync(document, pdf, options, _data);
        Assert.Equal(expected, summary.Output);
        Assert.Equal(0, CosDocument.Load(summary.Output).GetPages()[0].Rotate);
        Assert.Equal(pages.Sum(p => p.Words.Count), summary.Words);
        Assert.Equal(pages.Count, summary.PagesRecognised);
    }

    private async Task<string> CreateScanAsync(int rotation)
    {
        // Synthetic text only; independent WPF rotation of the pixels supplied to the PDF writer.
        var image = Sta.Run(() =>
        {
            var source = ScanOrientationRealTests.TextPage(false).Bitmap;
            var bitmap = BitmapSource.Create(source.Width, source.Height, 96, 96,
                PixelFormats.Bgra32, null, source.Pixels, source.Stride);
            var rotated = new TransformedBitmap(bitmap, new RotateTransform(rotation));
            var pixels = new byte[rotated.PixelWidth * rotated.PixelHeight * 4];
            rotated.CopyPixels(pixels, rotated.PixelWidth * 4, 0);
            return new PdfImage(rotated.PixelWidth, rotated.PixelHeight, pixels);
        });
        var path = Path.Combine(_folder.FullName, "scan.pdf");
        await _engine.CreateFromImagesAsync([new PdfBitmapInput(image)], new(PdfPageSizeMode.MatchImage, 0, 300), path);
        return path;
    }
}

public sealed class InstalledOcrOrientationTheoryAttribute : TheoryAttribute
{
    public InstalledOcrOrientationTheoryAttribute()
    {
        var data = new OcrLanguageData();
        if (!data.IsInstalled("eng") || !data.IsInstalled("osd"))
            Skip = "eng.traineddata or osd.traineddata is not installed.";
    }
}

public sealed class InstalledEnglishOcrFactAttribute : FactAttribute
{
    public InstalledEnglishOcrFactAttribute()
    {
        if (!new OcrLanguageData().IsInstalled("eng"))
            Skip = "eng.traineddata is not installed.";
    }
}
