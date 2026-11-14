using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Ocr;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class TesseractOcrEngineTests
{
    [Fact]
    public void Gray_bitmap_states_300_dpi_unless_Tesseract_should_estimate_the_resolution()
    {
        var bitmap = new PdfBitmap(2, 2, 8, new byte[16]);
        // BITMAPINFOHEADER biXPelsPerMeter / biYPelsPerMeter at offsets 38 and 42.
        var hinted = TesseractOcrEngine.GrayBmp(bitmap);
        Assert.Equal(11811, BitConverter.ToInt32(hinted, 38));
        Assert.Equal(11811, BitConverter.ToInt32(hinted, 42));
        var estimated = TesseractOcrEngine.GrayBmp(bitmap, resolutionHint: false);
        Assert.Equal(0, BitConverter.ToInt32(estimated, 38));
        Assert.Equal(0, BitConverter.ToInt32(estimated, 42));
    }

    [Fact]
    public void Joining_keeps_low_confidence_English_and_Thai_runs_and_their_boxes()
    {
        OcrWord[] pieces =
        [
            new("ค้น", new(.1, .1, .04, .03), 90, false),
            new("หาใน", new(.14, .09, .06, .04), 80, false),
            new("YouTube", new(.25, .1, .15, .03), 0, false),
            new("รู้จัก", new(.45, .09, .07, .04), 10, false),
            new("ผ่านเพื่อน", new(.52, .1, .12, .03), 0, false),
        ];
        var lineBox = new NormalizedRect(.1, .09, .54, .04);
        const string line = "ค้นหาใน YouTube รู้จักผ่านเพื่อน\n";
        var words = TesseractOcrEngine.MergeLinePieces(pieces, line, 7, 65, lineBox);

        Assert.Equal(Compact(line), Compact(string.Concat(words.Select(w => w.Text))));
        Assert.Equal(new[] { "ค้นหาใน", "YouTube", "รู้จักผ่านเพื่อน" }, words.Select(w => w.Text));
        Assert.Equal(new[] { true, true, false }, words.Select(w => w.SpaceAfter));
        Assert.Equal(new[] { 85f, 0f, 5f }, words.Select(w => w.Confidence));
        Assert.Equal(.1, words[0].Bounds.X);
        Assert.Equal(.1, words[0].Bounds.Width, 10);
        Assert.Equal(pieces[2].Bounds.X, words[1].Bounds.X);
        Assert.Equal(pieces[2].Bounds.Width, words[1].Bounds.Width, 10);
        Assert.All(words, w =>
        {
            Assert.Equal(7, w.Line);
            Assert.Equal(65, w.LineConfidence);
            Assert.Equal(lineBox, w.LineBounds);
        });
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\u00a0")]
    public void Joining_handles_repeated_pieces_marks_and_whitespace(string separator)
    {
        string[] text = ["ก", "้", "ก", "้", "YouTube", "ก", "้"];
        var pieces = text.Select((t, i) => new OcrWord(t, new(.1 + i * .05, .1, .05, .02), 0, false)).ToArray();
        var line = "ก้ก้" + separator + "YouTube" + separator + "ก้\n";
        var words = TesseractOcrEngine.MergeLinePieces(pieces, line);
        Assert.Equal(new[] { "ก้ก้", "YouTube", "ก้" }, words.Select(w => w.Text));
        Assert.Equal(new[] { true, true, false }, words.Select(w => w.SpaceAfter));
        Assert.Equal(Compact(line), Compact(string.Concat(words.Select(w => w.Text))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Line_text_is_preserved_when_pieces_are_missing_or_disagree(bool missing)
    {
        var lineBox = new NormalizedRect(.1, .2, .7, .04);
        OcrWord[] pieces = missing ? [] : [new("ค้นหาใน", new(.1, .2, .2, .04), 5, false)];
        const string line = "ค้นหาใน YouTube รู้จักผ่านเพื่อน\n";
        var word = Assert.Single(TesseractOcrEngine.MergeLinePieces(pieces, line, 2, 40, lineBox));
        Assert.Equal(line.Trim(), word.Text);
        Assert.Equal(lineBox, word.Bounds);
        Assert.Equal(missing ? 40 : 5, word.Confidence);
        Assert.Empty(TesseractOcrEngine.MergeLinePieces(pieces, " \n"));
        Assert.Empty(TesseractOcrEngine.MergeLinePieces([], ""));
    }

    [Fact]
    public void Options_keep_legacy_segmentation_by_default() =>
        Assert.Equal(OcrSegmentationMode.Auto, new OcrOptions(["tha", "eng"]).SegmentationMode);

    internal static string Compact(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
