namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>
/// The page text as it should read, for copying and searching: the same Thai repairs as exporting
/// (see <see cref="ReadTextSpans"/>), but kept in PDFium's character order so each character still knows where
/// it is on the page.
/// </summary>
internal sealed partial class PdfiumDocument
{
    /// <summary>A character of the repaired text and the PDFium character it comes from.</summary>
    private readonly record struct ReadChar(char C, int Source);

    /// <summary>
    /// The page's characters with repeated /ActualText left out, "า" stored as "ำ" (and the reverse) put right,
    /// a sara am's circle (stored as a raised space) joined to its letter, and Thai marks in the order Thai is
    /// written. Null when nothing needs repairing, so PDFium's own text and search can be used.
    /// </summary>
    private static List<ReadChar>? RepairedText(IntPtr text, ushort[] chars, int count)
    {
        var repeated = RepeatedText(text, chars, count);
        var page = chars.AsSpan(0, count);
        var hasThai = page.ContainsAnyInRange((ushort)0x0E00, (ushort)0x0E7F);
        if (repeated is null && !hasThai)
        {
            return null;
        }

        var brokenSaraAa = hasThai ? BrokenSaraAa(text, chars, count) : _ => false;
        var result = new List<ReadChar>(count);
        var changed = repeated is not null;
        for (var i = 0; i < count; i++)
        {
            if (repeated?[i] == true)
            {
                continue;
            }

            var c = (char)chars[i];
            if (c == ' ' && hasThai && IsCircleOfSaraAm(text, chars, count, i, brokenSaraAa))
            {
                // The letter after it reads "ำ" (below); the circle itself is no character.
                changed = true;
                continue;
            }

            if (c == 'ำ' && brokenSaraAa(i) && !CircleNear(text, chars, count, i))
            {
                c = 'า';
                changed = true;
            }
            else if (c == 'า' && !brokenSaraAa(i) && CircleNear(text, chars, count, i))
            {
                c = 'ำ';
                changed = true;
            }

            result.Add(new ReadChar(c, i));
        }

        changed |= OrderMarks(result);
        return changed ? result : null;
    }

    /// <summary>A space at <paramref name="i"/> that is drawn as the circle of the sara am next to it.</summary>
    private static bool IsCircleOfSaraAm(IntPtr text, ushort[] chars, int count, int i, Func<int, bool> brokenSaraAa)
    {
        var next = NextLetterIndex(chars, count, i);
        if (next < 0)
        {
            return false;
        }

        // Before the letter: "ค" + circle + "า" (or "ำ" in a font that maps "า" to "ำ"). After it: the circle
        // follows its "ำ" or "า".
        var previous = PreviousLetterIndex(chars, i);
        var before = (chars[next] == 'า' && !brokenSaraAa(next)) || (chars[next] == 'ำ' && brokenSaraAa(next));
        var after = previous >= 0 && (char)chars[previous] is 'า' or 'ำ' && NextLetterIndex(chars, count, previous) == next;
        return (before || after) && RaisedOverThaiLetter(text, chars, i);
    }

    /// <summary>Whether the sara aa (or am) at <paramref name="i"/> has its circle as a raised space just before or after it.</summary>
    private static bool CircleNear(IntPtr text, ushort[] chars, int count, int i)
    {
        for (var j = i - 1; j >= Math.Max(0, i - 2); j--)
        {
            if (chars[j] == ' ')
            {
                return RaisedOverThaiLetter(text, chars, j);
            }

            if (chars[j] is not ('\r' or '\n' or 0))
            {
                break;
            }
        }

        for (var j = i + 1; j < Math.Min(count, i + 3); j++)
        {
            if (chars[j] == ' ')
            {
                return RaisedOverThaiLetter(text, chars, j);
            }

            if (chars[j] is not ('\r' or '\n' or 0))
            {
                break;
            }
        }

        return false;
    }

    /// <summary>A "space" with a box raised above the line, over one of the Thai consonants just before it.</summary>
    private static bool RaisedOverThaiLetter(IntPtr text, ushort[] chars, int i)
    {
        if (PdfiumNative.FPDFText_GetCharBox(text, i, out var l, out var r, out var b, out _) == 0 || r <= l)
        {
            return false;
        }

        for (var j = i - 1; j >= Math.Max(0, i - 3); j--)
        {
            if (chars[j] is >= 0x0E01 and <= 0x0E2E)
            {
                var middle = (l + r) / 2;
                return PdfiumNative.FPDFText_GetCharBox(text, j, out var ll, out var lr, out var lb, out _) != 0
                    && middle >= ll - 0.5 && middle <= lr + 0.5 && b >= lb + (EffectiveFontSize(text, j) * 0.3);
            }
        }

        return false;
    }

    private static int NextLetterIndex(ushort[] chars, int count, int i)
    {
        for (var j = i + 1; j < count; j++)
        {
            if (!char.IsWhiteSpace((char)chars[j]) && chars[j] != 0)
            {
                return j;
            }
        }

        return -1;
    }

    private static int PreviousLetterIndex(ushort[] chars, int i)
    {
        for (var j = i - 1; j >= 0; j--)
        {
            if (!char.IsWhiteSpace((char)chars[j]) && chars[j] != 0)
            {
                return j;
            }
        }

        return -1;
    }

    /// <summary>Puts each run of Thai marks in written order (see <see cref="OrderThaiMarks"/>); true when one moved.</summary>
    private static bool OrderMarks(List<ReadChar> letters)
    {
        // The sorter works on positioned letters: each one carries its source index as its "left".
        var positioned = letters.Select(e => (e.C, Left: (double)e.Source, Right: 0.0, Mark: false)).ToList();
        OrderThaiMarks(positioned);
        var moved = false;
        for (var k = 0; k < letters.Count; k++)
        {
            var source = (int)positioned[k].Left;
            moved |= source != letters[k].Source;
            letters[k] = new ReadChar(positioned[k].C, source);
        }

        return moved;
    }
}
