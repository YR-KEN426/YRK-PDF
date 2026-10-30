using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>Amounts in words for receipts and quotations (Thai as Excel's BAHTTEXT writes it).</summary>
public sealed class AmountInWordsTests
{
    [Theory]
    [InlineData("10000", true)]
    [InlineData("12,500.50", true)]
    [InlineData("0.50", true)]
    [InlineData("฿1,000", true)]
    [InlineData("๑๐๐", true)]
    [InlineData("0812345678", false)] // A phone number.
    [InlineData("0105536000313", false)] // A tax ID.
    [InlineData("1104599001276", false)] // 13 digits.
    [InlineData("0", false)]
    [InlineData("ค่าเช่า", false)]
    [InlineData("รวม 1,500", false)]
    [InlineData("", false)]
    public void Knows_a_box_that_holds_just_an_amount(string text, bool expected) =>
        Assert.Equal(expected, AmountInWords.LooksLikeAmount(text));

    [Theory]
    [InlineData("10000", "หนึ่งหมื่นบาทถ้วน")]
    [InlineData("0", "ศูนย์บาทถ้วน")]
    [InlineData("1", "หนึ่งบาทถ้วน")]
    [InlineData("11", "สิบเอ็ดบาทถ้วน")]
    [InlineData("21", "ยี่สิบเอ็ดบาทถ้วน")]
    [InlineData("101", "หนึ่งร้อยเอ็ดบาทถ้วน")]
    [InlineData("120", "หนึ่งร้อยยี่สิบบาทถ้วน")]
    [InlineData("1234.50", "หนึ่งพันสองร้อยสามสิบสี่บาทห้าสิบสตางค์")]
    [InlineData("230,585.00", "สองแสนสามหมื่นห้าร้อยแปดสิบห้าบาทถ้วน")]
    [InlineData("1000000", "หนึ่งล้านบาทถ้วน")]
    [InlineData("1000001", "หนึ่งล้านเอ็ดบาทถ้วน")]
    [InlineData("21000000", "ยี่สิบเอ็ดล้านบาทถ้วน")]
    [InlineData("0.25", "ยี่สิบห้าสตางค์")]
    [InlineData("0.01", "หนึ่งสตางค์")]
    [InlineData("฿๑๒๕ บาท", "หนึ่งร้อยยี่สิบห้าบาทถ้วน")]
    [InlineData("-50", "ลบห้าสิบบาทถ้วน")]
    public void Writes_baht_in_thai(string typed, string expected)
    {
        Assert.True(AmountInWords.TryParse(typed, out var amount));
        Assert.Equal(expected, AmountInWords.Thai(amount));
    }

    [Theory]
    [InlineData("10000", "Ten Thousand Baht Only")]
    [InlineData("1234.50", "One Thousand Two Hundred Thirty-Four Baht and Fifty Satang")]
    [InlineData("2000015", "Two Million Fifteen Baht Only")]
    [InlineData("0.75", "Seventy-Five Satang")]
    public void Writes_baht_in_english(string typed, string expected)
    {
        Assert.True(AmountInWords.TryParse(typed, out var amount));
        Assert.Equal(expected, AmountInWords.English(amount));
    }

    [Theory]
    [InlineData("10000", 5, 0, 0, "10000")]
    [InlineData("รวม 1,500 บาท", 9, 0, 0, "1,500")]
    [InlineData("ค่าแรง 300 และค่าของ 2,000.50", 29, 0, 0, "2,000.50")]
    [InlineData("ค่าแรง 300 และค่าของ 2,000.50", 9, 0, 0, "300")]
    [InlineData("ราคา 300 รวม 500", 0, 13, 3, "500")]
    public void Finds_the_amount_being_typed(string text, int caret, int selectionStart, int selectionLength, string expected)
    {
        var found = AmountInWords.Find(text, caret, selectionStart, selectionLength);

        Assert.NotNull(found);
        Assert.Equal(expected, text.Substring(found.Value.Start, found.Value.Length));
    }

    [Fact]
    public void Finds_no_amount_in_words()
    {
        Assert.Null(AmountInWords.Find("หนึ่งหมื่นบาทถ้วน", 3));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12 34x")]
    public void Ignores_text_that_is_not_an_amount(string typed)
    {
        Assert.False(AmountInWords.TryParse(typed, out _));
    }
}
