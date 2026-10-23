namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

internal sealed partial class PdfiumDocument
{
    /// <summary>Longest text a repeated /ActualText copy has (a Thai cluster: a letter with up to three marks).</summary>
    private const int MaxRepeatLength = 4;

    /// <summary>
    /// Characters PDFium reports twice. Chrome and Edge ("Save as PDF") put a Thai letter and its marks, drawn as
    /// several glyphs, under one /ActualText; PDFium gives that text to every glyph, so "สัญญา" reads
    /// "สั ญ สั ญา" and cannot be found. A later copy of the same text whose box lies inside the first copy's box is
    /// such a repeat (a copy on the next line is not), and so is the space PDFium put after it. Null when the page has none.
    /// </summary>
    private static bool[]? RepeatedText(IntPtr text, ushort[] chars, int count)
    {
        var boxes = new (double Left, double Right, double Bottom, double Top)?[count];
        for (var i = 0; i < count; i++)
        {
            boxes[i] = PdfiumNative.FPDFText_GetCharBox(text, i, out var l, out var r, out var b, out var t) != 0 && r > l ? (l, r, b, t) : null;
        }

        // The box around a run of characters: null when any of them has no box.
        (double Left, double Right, double Bottom, double Top)? Around(int start, int length)
        {
            double left = double.MaxValue, right = double.MinValue, bottom = double.MaxValue, top = double.MinValue;
            for (var k = start; k < start + length; k++)
            {
                if (boxes[k] is not { } box)
                {
                    return null;
                }

                (left, right) = (Math.Min(left, box.Left), Math.Max(right, box.Right));
                (bottom, top) = (Math.Min(bottom, box.Bottom), Math.Max(top, box.Top));
            }

            return (left, right, bottom, top);
        }

        bool[]? repeated = null;
        for (var i = 1; i < count; i++)
        {
            if (char.IsWhiteSpace((char)chars[i]) || boxes[i] is null)
            {
                continue;
            }

            var length = RepeatLength(i);
            if (length == 0)
            {
                continue;
            }

            repeated ??= new bool[count];
            for (var k = i; k < i + length; k++)
            {
                repeated[k] = true;
            }

            i += length - 1;
            if (i + 1 < count && chars[i + 1] == ' ' && PdfiumNative.FPDFText_IsGenerated(text, i + 1) == 1)
            {
                repeated[++i] = true;
            }
        }

        return repeated;

        // How many characters from index i repeat an earlier copy close before them, 0 when none do.
        int RepeatLength(int i)
        {
            for (var length = 1; length <= MaxRepeatLength && i + length <= count; length++)
            {
                if (Around(i, length) is not { } copy)
                {
                    return 0;
                }

                // Only a Thai cluster, a letter with a mark, is drawn as several glyphs under one /ActualText;
                // other text that overlaps itself (a row of squeezed "=" signs) is not repeated.
                if (!chars.AsSpan(i, length).ContainsAnyInRange((ushort)0x0E31, (ushort)0x0E4E))
                {
                    continue;
                }

                var middle = (copy.Left + copy.Right) / 2;
                var level = (copy.Bottom + copy.Top) / 2;
                for (var j = Math.Max(0, i - (2 * MaxRepeatLength)); j + length <= i; j++)
                {
                    if (chars.AsSpan(j, length).SequenceEqual(chars.AsSpan(i, length))
                        && Around(j, length) is { } first && middle >= first.Left && middle <= first.Right
                        && Math.Abs(level - ((first.Bottom + first.Top) / 2)) < first.Top - first.Bottom)
                    {
                        return length;
                    }
                }
            }

            return 0;
        }
    }
}
