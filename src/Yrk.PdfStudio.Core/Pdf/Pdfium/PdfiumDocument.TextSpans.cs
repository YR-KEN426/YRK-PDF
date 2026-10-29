using System.Text;
using Yrk.PdfStudio.Core.Pdf.Export;

namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>Text with positions and styles, for exporting to Word, Excel and plain text.</summary>
internal sealed partial class PdfiumDocument
{
    /// <summary>How many letters back a Thai mark looks for the letter it sits on (see ReadTextSpans).</summary>
    private const int MarkSearchLetters = 64;

    public Task<PdfPageText> GetPageTextSpansAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        return _engine.InvokeAsync(() => ReadTextSpans(pageIndex), PdfWorkPriority.Background, cancellationToken);
    }

    private PdfPageText ReadTextSpans(int pageIndex)
    {
        var size = PageSizes[pageIndex];
        var spans = new List<PdfTextSpan>();
        var page = GetPage(pageIndex);
        var text = GetTextPage(pageIndex, page);
        var count = text == IntPtr.Zero ? 0 : PdfiumNative.FPDFText_CountChars(text);
        if (count <= 0)
        {
            return new PdfPageText(size.Width, size.Height, spans);
        }

        var (deviceWidth, deviceHeight) = GetDeviceSize(pageIndex);
        var chars = new ushort[count + 1];
        PdfiumNative.FPDFText_GetText(text, 0, count, chars);

        // Some PDFs (Excel's, through Windows printing) map the Thai letter "า" (sara aa) to "ำ" (sara am) in some
        // fonts: there they are swapped back (see BrokenSaraAa). A real sara am there is that letter followed by
        // its circle, mapped to a space drawn over the letter before.
        var brokenSaraAa = BrokenSaraAa(text, chars, count);
        // A character box in page space (left, right, bottom, top) → left, top, right, bottom in points on the page
        // as displayed.
        (double L, double T, double R, double B) Display(double l, double r, double b, double t)
        {
            var box = ToNormalized(page, deviceWidth, deviceHeight, l, t, r, b);
            return (box.X * size.Width, box.Y * size.Height, (box.X + box.Width) * size.Width, (box.Y + box.Height) * size.Height);
        }

        var repeated = RepeatedText(text, chars, count);
        var order = ReadingOrder(text, chars, count, brokenSaraAa, Display, repeated);

        // A span grows while the style stays the same and the characters follow each other on the line.
        // Each character keeps where it starts and ends on the page (for splitting at table columns).
        // The span's box is in displayed points (top above bottom).
        var current = new List<(char C, double Left, double Right, bool Mark)>();
        Style? style = null;
        double left = 0, top = 0, right = 0, bottom = 0;
        var hasBox = false;
        var circleBefore = false; // A sara am's circle was just read (see brokenSaraAa).
        var skippedBreak = false; // PDFium put a line break just before this character.

        void Flush()
        {
            OrderThaiMarks(current);
            var value = new string(current.Select(e => e.C).ToArray());
            if (style is { } s && hasBox && value.Trim().Length > 0)
            {
                spans.Add(new PdfTextSpan(
                    value,
                    left,
                    top,
                    right - left,
                    bottom - top,
                    s.Size,
                    s.Font,
                    s.Bold,
                    s.Italic,
                    s.Color,
                    current.Select(e => e.Left).ToList()));
            }

            current.Clear();
            hasBox = false;
            style = null;
        }

        (double Left, double Right) Across(double l, double r, double b, double t)
        {
            var box = ToNormalized(page, deviceWidth, deviceHeight, l, t, r, b);
            return (box.X * size.Width, (box.X + box.Width) * size.Width);
        }

        double End() => current.Count == 0 ? 0 : current[^1].Right;

        // Thai next to Latin letters or digits around index i: a space there is kept ("ออมทรัพย์ KBANK").
        bool ScriptChanges(int i)
        {
            var before = current.LastOrDefault(e => !e.Mark && e.C != ' ').C;
            var after = Enumerable.Range(i + 1, Math.Max(0, count - i - 1)).Select(j => (char)chars[j]).FirstOrDefault(ch => !char.IsWhiteSpace(ch) && !IsCombiningMark(ch));
            return before != default && after != default && IsThai(before) != IsThai(after) && char.IsLetterOrDigit(before) && char.IsLetterOrDigit(after);
        }

        // The next character after index i that is not a line break or space.
        char NextLetter(int i)
        {
            for (var j = i + 1; j < count; j++)
            {
                if (!char.IsWhiteSpace((char)chars[j]) && chars[j] != 0)
                {
                    return (char)chars[j];
                }
            }

            return default;
        }

        // A "space" at index i that is the circle of a sara am: raised above the line, over the Thai letter before it.
        bool RaisedCircle(int i)
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

        // The space between the text so far and the next letter (not a mark) after index i, in points.
        double GapAfter(int i)
        {
            for (var j = i + 1; j < count; j++)
            {
                var next = (char)chars[j];
                if (char.IsWhiteSpace(next) || IsCombiningMark(next))
                {
                    continue;
                }

                return PdfiumNative.FPDFText_GetCharBox(text, j, out var l, out var r, out var b, out var t) != 0 && r > l
                    ? Across(l, r, b, t).Left - End()
                    : double.MaxValue;
            }

            return double.MaxValue;
        }

        for (var n = 0; n < (order?.Count ?? count); n++)
        {
            var (i, c) = order is null ? (n, (char)chars[n]) : order[n];
            if (i >= 0 && repeated?[i] == true)
            {
                continue; // A second copy of a Thai cluster's /ActualText.
            }

            if (i < 0)
            {
                // Rebuilt reading order: a line break or a space between words.
                if (c == '\n')
                {
                    Flush();
                }
                else if (style is not null)
                {
                    current.Add((' ', End(), End(), false));
                }

                continue;
            }

            // PDFium puts CR LF between lines, but also between letters drawn one by one (as Excel's PDFs do): where
            // a line ends is decided below from where the letters are.
            if (c is '\r' or '\n' or '\0' or '￾')
            {
                skippedBreak = true;
                continue;
            }

            var hasCharBox = PdfiumNative.FPDFText_GetCharBox(text, i, out var l, out var r, out var b, out var t) != 0 && r > l && t > b;

            // A space drawn over the letter before a sara am is no space but the circle of that sara am.
            if (brokenSaraAa(i) && c == ' ' && hasCharBox && NextLetter(i) == (char)0x0E33 && current.FindLastIndex(e => e.C != ' ') is >= 0 and var lastIndex && current[lastIndex] is var last)
            {
                var (sl, _, sr, _) = Display(l, r, b, t);
                if ((sl + sr) / 2 >= last.Left - 0.5 && (sl + sr) / 2 <= last.Right + 0.5)
                {
                    circleBefore = true;
                    continue;
                }
            }

            // In other fonts too, a sara am can be drawn as its circle, a raised "space" over the letter before, and
            // "า" ("คำ" read "ค า", "สำ" read "สา "): together they are "ำ".
            if (!brokenSaraAa(i) && c == ' ' && NextLetter(i) == 'า' && RaisedCircle(i))
            {
                circleBefore = true;
                continue;
            }

            if (!brokenSaraAa(i) && c == 'า')
            {
                var after = i + 1;
                while (after < count && chars[after] is '\r' or '\n' or 0)
                {
                    after++;
                }

                if (circleBefore)
                {
                    c = 'ำ';
                }
                else if (order is null && after < count && chars[after] == ' ' && RaisedCircle(after))
                {
                    c = 'ำ';
                    n += after - i; // The circle is part of this letter.
                }
            }

            if (order is null && brokenSaraAa(i) && c == 'ำ')
            {
                // The circle may come next, after any line breaks PDFium put in.
                var after = i + 1;
                while (after < count && chars[after] is '\r' or '\n' or 0)
                {
                    after++;
                }

                var circleAfter = after < count && chars[after] == ' '
                    && PdfiumNative.FPDFText_GetCharBox(text, after, out var cl, out var cr, out _, out _) != 0 && (cl + cr) / 2 < l;
                if (circleAfter)
                {
                    n += after - i; // The circle belongs to this letter: it stays sara am.
                }
                else if (!circleBefore)
                {
                    c = 'า';
                }
            }

            if (!char.IsWhiteSpace(c) && !IsCombiningMark(c))
            {
                circleBefore = false;
            }

            if (char.IsWhiteSpace(c))
            {
                // PDFium adds spaces where letters are far apart, and also where a Thai mark stored late makes the
                // text jump back: a space it added with no gap after it is left out.
                if (style is { } spaceStyle && !(PdfiumNative.FPDFText_IsGenerated(text, i) == 1 && GapAfter(i) < spaceStyle.Size * 0.2 && !ScriptChanges(i)))
                {
                    current.Add((' ', End(), End(), false));
                }

                continue;
            }

            // A Thai vowel that follows its letter never starts a word: PDFium sometimes puts a space before it.
            if (TextLayout.FollowsLetter(c) && current.Count > 0 && current[^1].C == ' ')
            {
                current.RemoveAt(current.Count - 1);
            }

            // Thai vowels above and below and tone marks sit outside the line's box: they always stay with the
            // letter they are on (some PDFs, Excel's among them, store them after the next letter), and do not
            // stretch the span.
            if (style is not null && IsCombiningMark(c))
            {
                var at = current.Count;
                if (hasCharBox)
                {
                    var (markLeft, markRight) = Across(l, r, b, t);
                    // The letter it sits on: of the letters before it, the one under it whose middle is nearest. A Thai
                    // mark goes on a consonant (or a vowel above), never on a vowel written beside like "า". Excel can
                    // store the marks after a whole word ("เจ้าหนShopee mallี้" for "เจ้าหนี้Shopee mall"), so the
                    // search goes back further than the last few letters.
                    var middle = (markLeft + markRight) / 2;
                    var best = -1;
                    var bestDistance = double.MaxValue;
                    var bases = 0;
                    for (var j = current.Count - 1; j >= 0 && bases < MarkSearchLetters; j--)
                    {
                        if (current[j].Mark || current[j].C == ' ')
                        {
                            continue;
                        }

                        bases++;
                        if (IsThai(c) && IsThai(current[j].C) && !CarriesMarks(current[j].C))
                        {
                            continue;
                        }

                        var distance = Math.Abs(middle - ((current[j].Left + current[j].Right) / 2));
                        if (middle >= current[j].Left - 1 && middle <= current[j].Right + 1 && distance < bestDistance)
                        {
                            (best, bestDistance) = (j, distance);
                        }
                    }

                    if (best >= 0)
                    {
                        at = best + 1;
                        while (at < current.Count && current[at].Mark)
                        {
                            at++;
                        }
                    }
                }

                var host = at > 0 ? current[at - 1] : (C: c, Left: End(), Right: End(), Mark: true);
                current.Insert(at, (c, host.Left, host.Right, true));
                continue;
            }

            var charStyle = ReadStyle(text, i);

            // A new span for a style change, or a jump back (new column) or far ahead on the line. Measured on the
            // page as displayed, so turned pages (landscape by /Rotate) read along their lines too.
            var (dl, dt, dr, db) = hasCharBox ? Display(l, r, b, t) : default;
            if (style is { } open && hasBox && hasCharBox)
            {
                var gap = dl - right;
                if (!charStyle.SameAs(open) || gap > open.Size * 1.5 || gap < -open.Size || dt > bottom || db < top)
                {
                    Flush();
                }
            }
            else if (style is { } previous && !charStyle.SameAs(previous))
            {
                Flush();
            }

            style ??= charStyle;

            // Where PDFium put a line break between letters on one line, a visible gap is a space. So is one between
            // Thai and Latin letters, which PDFium may join after a Thai mark ("ออมทรัพย์ KBANK" printed from Excel).
            var scriptChange = current.FindLast(e => !e.Mark && e.C != ' ') is { C: not '\0' } previousLetter
                && char.IsLetterOrDigit(previousLetter.C) && char.IsLetterOrDigit(c) && IsThai(previousLetter.C) != IsThai(c);
            if ((skippedBreak || scriptChange) && hasCharBox && current.Count > 0 && current[^1].C != ' ' && dl - End() > charStyle.Size * 0.25)
            {
                current.Add((' ', End(), End(), false));
            }

            skippedBreak = false;
            current.Add((c, hasCharBox ? dl : End(), hasCharBox ? dr : End(), false));
            if (hasCharBox)
            {
                if (!hasBox)
                {
                    (left, top, right, bottom) = (dl, dt, dr, db);
                    hasBox = true;
                }
                else
                {
                    left = Math.Min(left, dl);
                    right = Math.Max(right, dr);
                    top = Math.Min(top, dt);
                    bottom = Math.Max(bottom, db);
                }
            }
        }

        Flush();
        return new PdfPageText(size.Width, size.Height, spans);
    }

    /// <summary>
    /// The characters in reading order when the page stores them out of order (PDFs printed from Excel draw some
    /// letters of a line later than the rest, and Thai marks apart from their letters): lines top to bottom, letters
    /// left to right, each mark after the letter it sits on, a space (index -1) where letters are a space apart and a
    /// line break (index -1) between lines. Null when the page's own order is fine.
    /// </summary>
    /// <param name="display">A character box (left, right, bottom, top in page space) → left, top, right, bottom in points.</param>
    private static List<(int Index, char C)>? ReadingOrder(
        IntPtr text, ushort[] chars, int count, Func<int, bool> brokenSaraAa, Func<double, double, double, double, (double L, double T, double R, double B)> display,
        bool[]? repeated)
    {
        var items = new List<(int I, char C, double L, double T, double R, double B)>();
        for (var i = 0; i < count; i++)
        {
            var c = (char)chars[i];
            if (c is '\r' or '\n' or '\0' or '￾' || repeated?[i] == true
                || PdfiumNative.FPDFText_GetCharBox(text, i, out var l, out var r, out var b, out var t) == 0 || r <= l || t <= b)
            {
                continue;
            }

            var (dl, dt, dr, db) = display(l, r, b, t);
            items.Add((i, c, dl, dt, dr, db));
        }


        static bool SameLine((int I, char C, double L, double T, double R, double B) a, (int I, char C, double L, double T, double R, double B) b) =>
            Math.Min(a.B, b.B) - Math.Max(a.T, b.T) >= 0.5 * Math.Min(a.B - a.T, b.B - b.T);

        // Out of order: letters jumping back along a line in at least one pair in ten.
        var letters = items.Where(x => !char.IsWhiteSpace(x.C) && !IsCombiningMark(x.C)).ToList();
        int pairs = 0, back = 0;
        for (var k = 1; k < letters.Count; k++)
        {
            if (SameLine(letters[k - 1], letters[k]))
            {
                pairs++;
                back += letters[k].L < letters[k - 1].L - 1 ? 1 : 0;
            }
        }

        if (pairs < 10 || back * 10 < pairs)
        {
            return null;
        }

        // Lines: letters overlapping in height, top to bottom; each line left to right.
        var lines = new List<List<(int I, char C, double L, double T, double R, double B)>>();
        foreach (var letter in letters.OrderBy(x => (x.T + x.B) / 2))
        {
            var line = lines.LastOrDefault(candidate => candidate.Any(other => SameLine(other, letter)));
            if (line is null)
            {
                lines.Add([letter]);
            }
            else
            {
                line.Add(letter);
            }
        }

        // Each mark goes after the letter it sits on: the one under (or over) its middle, a consonant for Thai.
        var marks = items.Where(x => IsCombiningMark(x.C)).ToList();
        var hosted = new Dictionary<int, List<char>>();
        foreach (var mark in marks)
        {
            var middle = (mark.L + mark.R) / 2;
            var host = letters
                .Where(x => middle >= x.L - 1 && middle <= x.R + 1 && Math.Abs(((x.T + x.B) / 2) - ((mark.T + mark.B) / 2)) < (x.B - x.T) * 1.6)
                .OrderBy(x => IsThai(mark.C) && IsThai(x.C) && !CarriesMarks(x.C) ? 1 : 0)
                .ThenBy(x => Math.Abs(((x.L + x.R) / 2) - middle))
                .Select(x => (int?)x.I)
                .FirstOrDefault();
            if (host is { } index)
            {
                (hosted.TryGetValue(index, out var list) ? list : hosted[index] = []).Add(mark.C);
            }
        }

        // A sara am's circle, when "า" is mapped to "ำ" (see brokenSaraAa): a space drawn over the letter before.
        var circles = items.Where(x => x.C == ' ').ToList();
        var order = new List<(int Index, char C)>();
        foreach (var line in lines)
        {
            line.Sort((a, b) => a.L.CompareTo(b.L));
            for (var k = 0; k < line.Count; k++)
            {
                var (index, c, l, _, _, _) = line[k];
                if (k > 0)
                {
                    var previous = line[k - 1];
                    if (l - previous.R > EffectiveFontSize(text, index) * 0.22)
                    {
                        order.Add((-1, ' '));
                    }

                    if (brokenSaraAa(index) && c == 'ำ')
                    {
                        var circle = circles.Any(x => (x.L + x.R) / 2 >= previous.L - 1 && (x.L + x.R) / 2 <= previous.R + 1 && SameLine(x, previous));
                        c = circle ? 'ำ' : 'า';
                    }
                }
                else if (brokenSaraAa(index) && c == 'ำ')
                {
                    c = 'า';
                }

                order.Add((index, c));
                foreach (var mark in hosted.GetValueOrDefault(index) ?? [])
                {
                    order.Add((marks.First(m => m.C == mark && !order.Contains((m.I, m.C))).I, mark));
                }
            }

            order.Add((-1, '\n'));
        }

        return order;
    }

    private static bool IsThai(char c) => c is >= (char)0x0E00 and <= (char)0x0E7F;

    /// <summary>
    /// Thai marks on one letter in the order Thai is written: the vowel above or below first, then the tone mark,
    /// then the thanthakhat. Some PDFs store them the other way ("น้ี" for "นี้", "ด์ิ" for "ดิ์"), which reads
    /// the same on the page but not in search or Word.
    /// </summary>
    private static void OrderThaiMarks(List<(char C, double Left, double Right, bool Mark)> letters)
    {
        static int Rank(char c) => c switch
        {
            'ั' or (>= 'ิ' and <= 'ฺ') or '็' => 0,
            >= '่' and <= '๋' => 1,
            _ => 2,
        };

        for (var start = 0; start < letters.Count; start++)
        {
            var end = start;
            while (end < letters.Count && IsThai(letters[end].C) && IsCombiningMark(letters[end].C))
            {
                end++;
            }

            if (end - start > 1)
            {
                var marks = letters.GetRange(start, end - start).OrderBy(e => Rank(e.C)).ToList(); // Stable.
                for (var k = 0; k < marks.Count; k++)
                {
                    letters[start + k] = marks[k];
                }
            }

            start = Math.Max(start, end);
        }
    }

    /// <summary>
    /// Which characters are in a font that maps "า" to "ำ". A page with many "ำ" and no "า" has it in every font.
    /// Otherwise a font has it when at least three of its "ำ" are no taller than the letter before them (a real
    /// sara am carries its circle above the letters; the "า" glyph does not) and none is, and it has no "า":
    /// the bold font of a letter can be broken while its regular text is fine.
    /// </summary>
    private static Func<int, bool> BrokenSaraAa(IntPtr text, ushort[] chars, int count)
    {
        var page = chars.AsSpan(0, count);
        if (page.Count((ushort)0x0E33) >= 5 && !page.Contains((ushort)0x0E32))
        {
            return _ => true;
        }

        if (!page.Contains((ushort)0x0E33))
        {
            return _ => false;
        }

        var fonts = new Dictionary<string, (int Flat, int Raised, int Aa)>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            if (chars[i] is not (0x0E32 or 0x0E33))
            {
                continue;
            }

            var font = FontInfo(text, i).Name;
            var (flat, raised, aa) = fonts.GetValueOrDefault(font);
            if (chars[i] == 0x0E32)
            {
                aa++;
            }
            else if (i > 0 && chars[i - 1] is >= 0x0E01 and <= 0x0E2E
                && PdfiumNative.FPDFText_GetCharBox(text, i, out _, out _, out _, out var top) != 0
                && PdfiumNative.FPDFText_GetCharBox(text, i - 1, out _, out _, out _, out var letterTop) != 0)
            {
                if (top <= letterTop + (EffectiveFontSize(text, i) * 0.12))
                {
                    flat++;
                }
                else
                {
                    raised++;
                }
            }

            fonts[font] = (flat, raised, aa);
        }

        var broken = fonts.Where(f => f.Value is { Flat: >= 3, Raised: 0, Aa: 0 }).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        return broken.Count == 0 ? _ => false : i => broken.Contains(FontInfo(text, i).Name);
    }

    /// <summary>Thai consonants (and the vowels written above or below), which carry tone marks and vowels above.</summary>
    private static bool CarriesMarks(char c) => c is >= (char)0x0E01 and <= (char)0x0E2E || IsCombiningMark(c);

    private static bool IsCombiningMark(char c) =>
        System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.EnclosingMark;

    private Style ReadStyle(IntPtr text, int index)
    {
        var size = EffectiveFontSize(text, index);
        var (font, flags) = FontInfo(text, index);
        var weight = PdfiumNative.FPDFText_GetFontWeight(text, index);
        PdfiumNative.FPDFText_GetFillColor(text, index, out var red, out var green, out var blue, out _);
        return new Style(
            size > 0 ? Math.Round(size * 2) / 2 : 12,
            font,
            weight >= 600 || (flags & ForceBoldFlag) != 0 || font.Contains("Bold", StringComparison.OrdinalIgnoreCase),
            (flags & ItalicFlag) != 0 || font.Contains("Italic", StringComparison.OrdinalIgnoreCase) || font.Contains("Oblique", StringComparison.OrdinalIgnoreCase),
            new PdfColor((byte)red, (byte)green, (byte)blue));
    }

    private sealed record Style(double Size, string Font, bool Bold, bool Italic, PdfColor Color)
    {
        public bool SameAs(Style other) =>
            Math.Abs(Size - other.Size) < 0.6 && Font == other.Font && Bold == other.Bold && Italic == other.Italic && Color == other.Color;
    }
}
