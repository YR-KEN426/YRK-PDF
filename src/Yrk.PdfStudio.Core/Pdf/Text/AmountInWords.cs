using System.Globalization;
using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Text;

/// <summary>
/// An amount of money in words, as written on Thai receipts and quotations: 10000 is "หนึ่งหมื่นบาทถ้วน" (like
/// Excel's BAHTTEXT) or "Ten Thousand Baht Only".
/// </summary>
public static partial class AmountInWords
{
    private static readonly string[] ThaiDigits = ["", "หนึ่ง", "สอง", "สาม", "สี่", "ห้า", "หก", "เจ็ด", "แปด", "เก้า"];
    private static readonly string[] ThaiPlaces = ["", "สิบ", "ร้อย", "พัน", "หมื่น", "แสน"];

    private static readonly string[] Ones =
    [
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
        "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    ];

    private static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];
    private static readonly string[] Scales = ["", "Thousand", "Million", "Billion", "Trillion"];

    /// <summary>Reads an amount typed by hand: "10,000", "1234.50", "฿500", "๑๒๓ บาท".</summary>
    public static bool TryParse(string? text, out decimal amount)
    {
        amount = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = new StringBuilder();
        foreach (var c in text.Trim())
        {
            if (c is >= '๐' and <= '๙')
            {
                cleaned.Append((char)('0' + (c - '๐')));
            }
            else if (c is ',' or ' ' or '฿' or ' ')
            {
                // Thousands separators and the baht sign.
            }
            else
            {
                cleaned.Append(c);
            }
        }

        var number = cleaned.ToString();
        foreach (var unit in (string[])["บาท", "THB", "Baht", "baht"])
        {
            if (number.EndsWith(unit, StringComparison.Ordinal))
            {
                number = number[..^unit.Length];
            }
        }

        return decimal.TryParse(number, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
            && Math.Abs(amount) < 1_000_000_000_000_000m;
    }

    /// <summary>
    /// The amount to write out in a text being typed: the selection when it is one, else the whole text when it is
    /// one, else the number at or just before the caret ("รวม 1,500" → "1,500").
    /// </summary>
    /// <returns>Where the amount is in the text, or null when there is none.</returns>
    public static (int Start, int Length, decimal Amount)? Find(string text, int caret, int selectionStart = 0, int selectionLength = 0)
    {
        if (selectionLength > 0 && TryParse(text.Substring(selectionStart, selectionLength), out var selected))
        {
            return (selectionStart, selectionLength, selected);
        }

        if (TryParse(text, out var whole))
        {
            return (0, text.Length, whole);
        }

        (int, int, decimal)? found = null;
        foreach (System.Text.RegularExpressions.Match match in NumberPattern().Matches(text))
        {
            if (match.Index > caret)
            {
                break;
            }

            var number = match.Value.TrimEnd(',', '.');
            if (TryParse(number, out var amount))
            {
                found = (match.Index, number.Length, amount);
            }
        }

        return found;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"-?[0-9๐-๙][0-9๐-๙,]*(\.[0-9๐-๙]+)?")]
    private static partial System.Text.RegularExpressions.Regex NumberPattern();

    /// <summary>"หนึ่งพันสองร้อยสามสิบสี่บาทห้าสิบสตางค์"; whole baht end in "ถ้วน".</summary>
    public static string Thai(decimal amount)
    {
        var (negative, baht, satang) = Split(amount);
        var text = new StringBuilder(negative ? "ลบ" : string.Empty);
        if (baht == 0 && satang == 0)
        {
            return text.Append("ศูนย์บาทถ้วน").ToString();
        }

        if (baht > 0)
        {
            text.Append(ThaiNumber(baht)).Append("บาท");
        }

        return (satang == 0 ? text.Append("ถ้วน") : text.Append(ThaiNumber(satang)).Append("สตางค์")).ToString();
    }

    /// <summary>"One Thousand Two Hundred Thirty-Four Baht and Fifty Satang"; whole baht end in "Only".</summary>
    public static string English(decimal amount)
    {
        var (negative, baht, satang) = Split(amount);
        var sign = negative ? "Minus " : string.Empty;
        if (satang == 0)
        {
            return sign + EnglishNumber(baht) + " Baht Only";
        }

        return baht == 0
            ? sign + EnglishNumber(satang) + " Satang"
            : sign + EnglishNumber(baht) + " Baht and " + EnglishNumber(satang) + " Satang";
    }

    private static (bool Negative, long Baht, long Satang) Split(decimal amount)
    {
        var rounded = Math.Round(Math.Abs(amount), 2, MidpointRounding.AwayFromZero);
        var baht = (long)Math.Floor(rounded);
        return (amount < 0 && rounded > 0, baht, (long)((rounded - baht) * 100));
    }

    /// <summary>Thai reads numbers in groups of six digits, each followed by ล้าน.</summary>
    private static string ThaiNumber(long n)
    {
        if (n >= 1_000_000)
        {
            return ThaiNumber(n / 1_000_000) + "ล้าน" + ThaiBelowMillion(n % 1_000_000, hasHigher: true);
        }

        return ThaiBelowMillion(n, hasHigher: false);
    }

    private static string ThaiBelowMillion(long n, bool hasHigher)
    {
        var text = new StringBuilder();
        var digits = n.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < digits.Length; i++)
        {
            var digit = digits[i] - '0';
            var place = digits.Length - 1 - i;
            if (digit == 0)
            {
                continue;
            }

            text.Append(place switch
            {
                1 when digit == 1 => "สิบ",
                1 when digit == 2 => "ยี่สิบ",

                // A one in the units after any higher digit is เอ็ด: สิบเอ็ด, หนึ่งร้อยเอ็ด, หนึ่งล้านเอ็ด.
                0 when digit == 1 && (hasHigher || n >= 10) => "เอ็ด",
                _ => ThaiDigits[digit] + ThaiPlaces[place],
            });
        }

        return text.ToString();
    }

    private static string EnglishNumber(long n)
    {
        if (n == 0)
        {
            return Ones[0];
        }

        var parts = new List<string>();
        for (var scale = 0; n > 0; scale++, n /= 1000)
        {
            if (n % 1000 is var group and > 0)
            {
                parts.Insert(0, EnglishBelowThousand((int)group) + (Scales[scale].Length > 0 ? " " + Scales[scale] : string.Empty));
            }
        }

        return string.Join(" ", parts);
    }

    private static string EnglishBelowThousand(int n)
    {
        var parts = new List<string>();
        if (n >= 100)
        {
            parts.Add(Ones[n / 100] + " Hundred");
            n %= 100;
        }

        if (n >= 20)
        {
            parts.Add(Tens[n / 10] + (n % 10 > 0 ? "-" + Ones[n % 10] : string.Empty));
        }
        else if (n > 0)
        {
            parts.Add(Ones[n]);
        }

        return string.Join(" ", parts);
    }
}
