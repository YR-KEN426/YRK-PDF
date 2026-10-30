using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App.Imaging;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class PictureImagingTests
{
    [Fact]
    public void Large_pictures_are_scaled_down_and_keep_their_shape() => Sta.Run(() =>
    {
        var result = PictureImaging.Prepare(Blank(4800, 1200));

        Assert.Equal(2400, result.PixelWidth);
        Assert.Equal(600, result.PixelHeight);
        Assert.Equal(PixelFormats.Bgra32, result.Format);
        Assert.True(result.IsFrozen);
        return true;
    });

    [Fact]
    public void A_small_picture_keeps_its_pixels() => Sta.Run(() =>
    {
        var result = PictureImaging.Prepare(Blank(300, 200));

        Assert.Equal(300, result.PixelWidth);
        Assert.Equal(200, result.PixelHeight);
        return true;
    });

    [Fact]
    public void Fits_inside_the_page() => Sta.Run(() =>
    {
        var (smallWidth, smallHeight) = PictureImaging.FitToPage(Blank(300, 150), 595, 842);
        Assert.Equal(144, smallWidth, 3); // 300 pixels at 150 dpi.
        Assert.Equal(72, smallHeight, 3);

        var (width, height) = PictureImaging.FitToPage(Blank(2400, 1200), 595, 842);
        Assert.Equal(357, width, 3); // 60% of the page.
        Assert.Equal(178.5, height, 3);
        return true;
    });

    [Theory]
    [InlineData("a.PNG", true)]
    [InlineData("a.jpeg", true)]
    [InlineData("a.pdf", false)]
    [InlineData("a", false)]
    public void Knows_picture_files(string path, bool expected) => Assert.Equal(expected, PictureImaging.IsPicture(path));

    private static BitmapSource Blank(int width, int height) =>
        BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, new byte[width * height * 3], width * 3);
}
