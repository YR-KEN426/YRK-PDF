using System.Globalization;

namespace Yrk.PdfStudio.Core.Pdf.Archive;

/// <summary>Glyph names used in font /Differences (Adobe Glyph List, the Latin part) → characters.</summary>
internal static class GlyphNames
{
    private static readonly Dictionary<string, char> Names = new(StringComparer.Ordinal)
    {
        ["space"] = ' ', ["exclam"] = '!', ["quotedbl"] = '"', ["numbersign"] = '#', ["dollar"] = '$', ["percent"] = '%',
        ["ampersand"] = '&', ["quotesingle"] = '\'', ["quoteright"] = '’', ["parenleft"] = '(', ["parenright"] = ')',
        ["asterisk"] = '*', ["plus"] = '+', ["comma"] = ',', ["hyphen"] = '-', ["minus"] = '−', ["period"] = '.', ["slash"] = '/',
        ["zero"] = '0', ["one"] = '1', ["two"] = '2', ["three"] = '3', ["four"] = '4', ["five"] = '5', ["six"] = '6',
        ["seven"] = '7', ["eight"] = '8', ["nine"] = '9', ["colon"] = ':', ["semicolon"] = ';', ["less"] = '<', ["equal"] = '=',
        ["greater"] = '>', ["question"] = '?', ["at"] = '@', ["bracketleft"] = '[', ["backslash"] = '\\', ["bracketright"] = ']',
        ["asciicircum"] = '^', ["underscore"] = '_', ["grave"] = '`', ["quoteleft"] = '‘', ["braceleft"] = '{', ["bar"] = '|',
        ["braceright"] = '}', ["asciitilde"] = '~', ["bullet"] = '•', ["endash"] = '–', ["emdash"] = '—', ["ellipsis"] = '…',
        ["quotedblleft"] = '“', ["quotedblright"] = '”', ["quotesinglbase"] = '‚', ["quotedblbase"] = '„', ["dagger"] = '†',
        ["daggerdbl"] = '‡', ["perthousand"] = '‰', ["trademark"] = '™', ["copyright"] = '©', ["registered"] = '®',
        ["degree"] = '°', ["plusminus"] = '±', ["multiply"] = '×', ["divide"] = '÷', ["section"] = '§', ["paragraph"] = '¶',
        ["euro"] = '€', ["Euro"] = '€', ["sterling"] = '£', ["yen"] = '¥', ["cent"] = '¢', ["currency"] = '¤',
        ["guillemotleft"] = '«', ["guillemotright"] = '»', ["guilsinglleft"] = '‹', ["guilsinglright"] = '›',
        ["exclamdown"] = '¡', ["questiondown"] = '¿', ["fi"] = 'ﬁ', ["fl"] = 'ﬂ', ["mu"] = 'µ', ["periodcentered"] = '·',
        ["germandbls"] = 'ß', ["ae"] = 'æ', ["AE"] = 'Æ', ["oe"] = 'œ', ["OE"] = 'Œ', ["oslash"] = 'ø', ["Oslash"] = 'Ø',
        ["dotlessi"] = 'ı', ["nbspace"] = ' ', ["onehalf"] = '½', ["onequarter"] = '¼', ["threequarters"] = '¾',
        ["ordfeminine"] = 'ª', ["ordmasculine"] = 'º', ["brokenbar"] = '¦', ["dieresis"] = '¨', ["macron"] = '¯',
        ["acute"] = '´', ["cedilla"] = '¸', ["logicalnot"] = '¬', ["florin"] = 'ƒ', ["circumflex"] = 'ˆ', ["tilde"] = '˜',
        ["Scaron"] = 'Š', ["scaron"] = 'š', ["Zcaron"] = 'Ž', ["zcaron"] = 'ž', ["Ydieresis"] = 'Ÿ', ["eth"] = 'ð',
        ["Eth"] = 'Ð', ["thorn"] = 'þ', ["Thorn"] = 'Þ',
    };

    private static readonly Dictionary<string, char> Accents = new(StringComparer.Ordinal)
    {
        ["acute"] = '́', ["grave"] = '̀', ["circumflex"] = '̂', ["dieresis"] = '̈', ["tilde"] = '̃',
        ["ring"] = '̊', ["cedilla"] = '̧', ["caron"] = '̌',
    };

    /// <summary>The character a glyph name stands for, or null when the name is not known.</summary>
    public static char? ToChar(string name)
    {
        if (name.Length == 1 && char.IsAsciiLetter(name[0]))
        {
            return name[0];
        }

        if (Names.TryGetValue(name, out var known))
        {
            return known;
        }

        // uniXXXX and uXXXX names.
        if ((name.StartsWith("uni", StringComparison.Ordinal) && name.Length == 7) || (name.StartsWith('u') && name.Length == 5))
        {
            var hex = name[^4..];
            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            {
                return (char)code;
            }
        }

        // Accented Latin letters: "eacute", "Udieresis", "aring", "ccedilla" …
        foreach (var (accent, mark) in Accents)
        {
            if (name.Length == accent.Length + 1 && name.EndsWith(accent, StringComparison.Ordinal) && char.IsAsciiLetter(name[0]))
            {
                var composed = string.Concat(name[0], mark).Normalize(System.Text.NormalizationForm.FormC);
                if (composed.Length == 1)
                {
                    return composed[0];
                }
            }
        }

        return null;
    }
}
