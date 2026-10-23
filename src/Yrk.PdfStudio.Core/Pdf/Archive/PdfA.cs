using System.Globalization;
using System.Security;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Archive;

/// <summary>An installed TrueType font the app found for a font a PDF uses but does not contain.</summary>
/// <param name="Data">The whole .ttf file (not a collection).</param>
/// <param name="AdvanceWidth">The width of a character in 1/1000 em, or null when the font has no glyph for it.</param>
/// <param name="Box">Font bounding box in 1/1000 em: left, bottom, right, top.</param>
public sealed record PdfAFont(
    string PostScriptName,
    byte[] Data,
    Func<char, double?> AdvanceWidth,
    double Ascent,
    double Descent,
    double CapHeight,
    double ItalicAngle,
    (double X0, double Y0, double X1, double Y1) Box,
    bool Bold);

/// <summary>Finds installed fonts (on Windows, the app looks them up through WPF).</summary>
public interface IPdfAFontSource
{
    /// <summary>The font to embed for a PDF font name such as "Helvetica-Bold" or "Tahoma,Italic"; null when none.</summary>
    PdfAFont? Find(string family, bool bold, bool italic);
}

/// <summary>A message in plain words: an English template (the key for its translation) and what fills it in.</summary>
public sealed record PdfANote(string Format, string? Argument = null)
{
    public override string ToString() => Argument is null ? Format : string.Format(CultureInfo.InvariantCulture, Format, Argument);
}

/// <summary>What the conversion did.</summary>
/// <param name="Changes">What was adjusted to meet the standard.</param>
public sealed record PdfAResult(byte[] Pdf, IReadOnlyList<PdfANote> Changes);

/// <summary>The document cannot be made PDF/A without changing what it shows; nothing was written.</summary>
public sealed class PdfAException(IReadOnlyList<PdfANote> problems)
    : PdfException("The document cannot be saved as PDF/A: " + string.Join("; ", problems))
{
    public IReadOnlyList<PdfANote> Problems { get; } = problems;
}

/// <summary>
/// Converts a PDF to PDF/A-2b (ISO 19005-2, level B: it looks the same in the future): fonts embedded, an
/// sRGB output intent, XMP metadata matching the document information, no JavaScript, external content or
/// hidden annotations. What cannot be fixed without changing the look (fonts that cannot be found, CMYK
/// colour, annotations without an appearance) is reported and nothing is written, so a file is never
/// labelled PDF/A when it is not.
/// </summary>
public static class PdfA
{
    private static readonly HashSet<string> ForbiddenAnnotations = new(StringComparer.Ordinal)
    {
        "Sound", "Movie", "Screen", "3D", "RichMedia", "FileAttachment", "TrapNet", "PrinterMark",
    };

    private static readonly HashSet<string> ForbiddenActions = new(StringComparer.Ordinal)
    {
        "Launch", "Sound", "Movie", "ResetForm", "ImportData", "JavaScript", "Hide", "SetOCGState", "Rendition", "Trans", "GoTo3DView",
    };

    /// <summary>The standard 14 fonts and the installed fonts with the same metrics.</summary>
    private static readonly Dictionary<string, string> MetricCompatible = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Helvetica"] = "Arial",
        ["Arial"] = "Arial",
        ["ArialMT"] = "Arial",
        ["Times"] = "Times New Roman",
        ["Times-Roman"] = "Times New Roman",
        ["TimesNewRoman"] = "Times New Roman",
        ["TimesNewRomanPSMT"] = "Times New Roman",
        ["Courier"] = "Courier New",
        ["CourierNew"] = "Courier New",
        ["CourierNewPSMT"] = "Courier New",
    };

    /// <exception cref="PdfAException">Something cannot be made compliant; the list says what.</exception>
    /// <exception cref="PdfException">The file cannot be read or is encrypted.</exception>
    public static PdfAResult Convert(byte[] pdf, IPdfAFontSource fonts, string? title = null, DateTimeOffset? now = null)
    {
        var document = CosDocument.Load(pdf);
        if (document.IsEncrypted)
        {
            throw new PdfException("Remove the password protection first.");
        }

        var job = new Job(document, fonts);
        job.Run(title, now ?? DateTimeOffset.Now);
        if (job.Problems.Count > 0)
        {
            throw new PdfAException(job.Problems.Distinct().ToList());
        }

        return new PdfAResult(CosCompactWriter.Write(document, version: "1.7"), job.Changes.Distinct().ToList());
    }

    private sealed class Job(CosDocument document, IPdfAFontSource fonts)
    {
        private readonly HashSet<CosObjectId> _visited = [];

        public List<PdfANote> Problems { get; } = [];

        public List<PdfANote> Changes { get; } = [];

        public void Run(string? title, DateTimeOffset now)
        {
            var catalog = document.Catalog;
            FixCatalog(catalog);
            foreach (var page in document.GetPages())
            {
                FixAnnotations(page.Dictionary);
            }

            Visit(document.Trailer["Root"], null);
            AddOutputIntent(catalog);
            WriteMetadata(catalog, title, now);
            if (document.Trailer["ID"] is not CosArray { Count: 2 })
            {
                var id = new CosString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16), isHex: true);
                document.Trailer["ID"] = new CosArray([id, id]);
            }
        }

        // --- Document level -----------------------------------------------------------------

        private void FixCatalog(CosDictionary catalog)
        {
            if (catalog["Version"] is CosName { Value: var version } && string.CompareOrdinal(version, "1.7") > 0)
            {
                catalog.Entries.Remove("Version"); // PDF/A-2 is based on PDF 1.7.
            }

            if (catalog.Entries.Remove("AA") | catalog.Entries.Remove("NeedsRendering"))
            {
                Changes.Add(new("Removed automatic actions"));
            }

            if (document.Resolve(catalog["OpenAction"]) is CosDictionary open && IsForbiddenAction(open))
            {
                catalog.Entries.Remove("OpenAction");
                Changes.Add(new("Removed a script that ran when the file opened"));
            }

            if (document.Resolve(catalog["Names"]) is CosDictionary names)
            {
                if (names.Entries.Remove("JavaScript"))
                {
                    Changes.Add(new("Removed JavaScript"));
                }

                if (names.Entries.Remove("EmbeddedFiles"))
                {
                    Changes.Add(new("Removed attached files (PDF/A keeps a document self-contained)"));
                }
            }

            if (document.Resolve(catalog["AcroForm"]) is CosDictionary form)
            {
                if (form.Entries.Remove("XFA"))
                {
                    Changes.Add(new("Removed the XFA form layer"));
                }

                if (form["NeedAppearances"] is CosBoolean { Value: true })
                {
                    form["NeedAppearances"] = new CosBoolean(false);
                }

                // A rewritten file breaks every digital signature: keep the fields, drop the now-invalid values.
                if (document.Resolve(form["Fields"]) is CosArray fields && RemoveSignatureValues(fields, 0))
                {
                    catalog.Entries.Remove("Perms");
                    form.Entries.Remove("SigFlags");
                    Changes.Add(new("Removed digital signatures, which a converted copy can no longer keep valid"));
                }
            }

            // Optional content (layers): every configuration needs a name and no automatic states.
            if (document.Resolve(catalog["OCProperties"]) is CosDictionary layers)
            {
                var configs = new List<CosDictionary>();
                if (document.Resolve(layers["D"]) is CosDictionary d)
                {
                    configs.Add(d);
                }

                if (document.Resolve(layers["Configs"]) is CosArray more)
                {
                    configs.AddRange(more.Items.Select(document.Resolve).OfType<CosDictionary>());
                }

                for (var i = 0; i < configs.Count; i++)
                {
                    configs[i].Entries.Remove("AS");
                    if (configs[i]["Name"] is not CosString)
                    {
                        configs[i]["Name"] = CosString.FromText(i == 0 ? "Layers" : $"Layers {i + 1}");
                    }
                }
            }
        }

        private bool RemoveSignatureValues(CosArray fields, int depth)
        {
            var removed = false;
            foreach (var field in fields.Items.Select(document.Resolve).OfType<CosDictionary>())
            {
                if (field["FT"] is CosName { Value: "Sig" } && field.Entries.Remove("V"))
                {
                    removed = true;
                }

                if (depth < 32 && document.Resolve(field["Kids"]) is CosArray kids)
                {
                    removed |= RemoveSignatureValues(kids, depth + 1);
                }
            }

            return removed;
        }

        private void FixAnnotations(CosDictionary page)
        {
            page.Entries.Remove("AA");
            if (document.Resolve(page["Annots"]) is not CosArray annotations)
            {
                return;
            }

            var kept = new List<CosObject>();
            foreach (var item in annotations.Items)
            {
                if (document.Resolve(item) is not CosDictionary annotation)
                {
                    continue;
                }

                var subtype = (annotation["Subtype"] as CosName)?.Value ?? string.Empty;
                if (ForbiddenAnnotations.Contains(subtype))
                {
                    Changes.Add(new("Removed {0} annotations (sound, video, attachments)", subtype));
                    continue;
                }

                annotation.Entries.Remove("AA");
                if (document.Resolve(annotation["A"]) is CosDictionary action && IsForbiddenAction(action))
                {
                    annotation.Entries.Remove("A");
                    Changes.Add(new("Removed actions that run scripts or programs"));
                }

                // Hidden ones are left out (showing them would change the look); the rest printable and visible.
                var flags = (annotation["F"] as CosInteger)?.Value ?? 0;
                if ((flags & 2) != 0 && subtype != "Popup")
                {
                    Changes.Add(new("Removed hidden comments"));
                    continue;
                }

                var fixedFlags = (flags & ~(1L | 2 | 32 | 256)) | 4;
                if (subtype != "Popup" && flags != fixedFlags)
                {
                    annotation["F"] = new CosInteger(fixedFlags);
                    Changes.Add(new("Made comments printable, as PDF/A requires"));
                }

                if (document.Resolve(annotation["AP"]) is CosDictionary appearance)
                {
                    appearance.Entries.Remove("R");
                    appearance.Entries.Remove("D");
                }
                else if (subtype is not ("Popup" or "Link") && !ZeroArea(annotation))
                {
                    Problems.Add(new("A {0} annotation has no appearance (open it in a full PDF editor and save, or delete it)", subtype));
                }

                kept.Add(item);
            }

            if (kept.Count != annotations.Count)
            {
                page["Annots"] = new CosArray(kept);
            }
        }

        private bool ZeroArea(CosDictionary annotation) =>
            document.ToRect(annotation["Rect"]) is not { } r || Math.Abs(r.X1 - r.X0) < 0.001 || Math.Abs(r.Y1 - r.Y0) < 0.001;

        private bool IsForbiddenAction(CosDictionary action) =>
            action["S"] is CosName { Value: var type } && ForbiddenActions.Contains(type);

        // --- Every object ------------------------------------------------------------------

        private void Visit(CosObject? value, string? key)
        {
            switch (value)
            {
                case CosReference reference:
                    if (_visited.Add(reference.Id) && document.GetObject(reference.Id) is { } target)
                    {
                        var replaced = target is CosStream stream ? FixStream(stream) : target;
                        if (!ReferenceEquals(replaced, target))
                        {
                            document.Replace(reference.Id, replaced);
                        }

                        Visit(replaced, key);
                    }

                    break;
                case CosArray array:
                    foreach (var item in array.Items)
                    {
                        Visit(item, key);
                    }

                    break;
                case CosStream stream:
                    FixDictionary(stream.Dictionary, key);
                    foreach (var (childKey, child) in stream.Dictionary.Entries.ToList())
                    {
                        Visit(child, childKey);
                    }

                    break;
                case CosDictionary dictionary:
                    FixDictionary(dictionary, key);
                    foreach (var (childKey, child) in dictionary.Entries.ToList())
                    {
                        Visit(child, key == "ExtGState" || key == "Font" ? key + "Entry" : childKey);
                    }

                    break;
            }
        }

        private void FixDictionary(CosDictionary dictionary, string? key)
        {
            if (dictionary.Entries.Remove("AA"))
            {
                Changes.Add(new("Removed automatic actions"));
            }

            if (key == "ExtGStateEntry")
            {
                if (dictionary.Entries.Remove("TR") | dictionary.Entries.Remove("HTP") |
                    (dictionary["TR2"] is not null and not CosName { Value: "Default" } && dictionary.Entries.Remove("TR2")))
                {
                    Changes.Add(new("Removed printer transfer functions"));
                }
            }

            if (dictionary["Type"] is CosName { Value: "Font" } || key == "FontEntry")
            {
                FixFont(dictionary);
            }

            switch ((dictionary["Subtype"] as CosName)?.Value)
            {
                case "Image":
                    if (dictionary["Interpolate"] is CosBoolean { Value: true })
                    {
                        dictionary["Interpolate"] = new CosBoolean(false);
                    }

                    dictionary.Entries.Remove("Alternates");
                    dictionary.Entries.Remove("OPI");
                    if (UsesCmyk(dictionary["ColorSpace"]))
                    {
                        Problems.Add(new("An image is in CMYK colour (PDF/A with an sRGB profile needs RGB or greyscale)"));
                    }

                    break;
                case "Form":
                    dictionary.Entries.Remove("OPI");
                    if (dictionary.Entries.Remove("Ref"))
                    {
                        Changes.Add(new("Removed links to content in other files"));
                    }

                    if (dictionary["Subtype2"] is CosName { Value: "PS" })
                    {
                        Problems.Add(new("The document contains PostScript code"));
                    }

                    break;
                case "PS":
                    Problems.Add(new("The document contains PostScript code"));
                    break;
            }

            if (dictionary["F"] is not null && dictionary["Length"] is not null)
            {
                Problems.Add(new("A stream refers to an external file"));
            }
        }

        private CosStream FixStream(CosStream stream)
        {
            var filters = stream.Dictionary["Filter"] switch
            {
                CosName name => [name.Value],
                CosArray array => array.Items.OfType<CosName>().Select(n => n.Value).ToList(),
                _ => new List<string>(),
            };
            if (filters.Any(f => f is "LZWDecode" or "LZW"))
            {
                // LZW is not allowed: decode and store with Flate instead.
                var data = CosFilters.Decode(stream, document.Resolve);
                var dictionary = stream.Dictionary.Clone();
                dictionary.Entries.Remove("DecodeParms");
                dictionary["Filter"] = new CosName("FlateDecode");
                Changes.Add(new("Recompressed LZW data with Flate"));
                return new CosStream(dictionary, CosFilters.Deflate(data));
            }

            // Content streams: CMYK colour operators.
            if (stream.Dictionary["Subtype"] is null or CosName { Value: "Form" } && stream.Dictionary["Type"] is null or CosName { Value: "XObject" }
                && stream.Dictionary["Length1"] is null && stream.Dictionary["Subtype"] is not CosName { Value: "Image" })
            {
                try
                {
                    var content = CosFilters.Decode(stream, document.Resolve);
                    if (LooksLikeContent(content) && ContentTokenizer.Parse(content).Any(op =>
                        op.Operator is "k" or "K" || (op.Operator is "cs" or "CS" && op.Operands.Count == 1 && op.Operands[0] is CosName { Value: "DeviceCMYK" })))
                    {
                        Problems.Add(new("Some content uses CMYK colour (PDF/A with an sRGB profile needs RGB or greyscale)"));
                    }
                }
                catch (PdfException)
                {
                    // Not content, or data we cannot decode: nothing to check here.
                }
            }

            return stream;
        }

        private static bool LooksLikeContent(byte[] content) =>
            content.Length > 0 && content.Take(256).All(b => b is 9 or 10 or 13 or (>= 32 and < 127));

        private bool UsesCmyk(CosObject? colorSpace) => document.Resolve(colorSpace) switch
        {
            CosName { Value: "DeviceCMYK" or "CMYK" } => true,
            CosArray array when array.Count > 1 && array[0] is CosName { Value: "Indexed" } => UsesCmyk(array[1]),
            _ => false,
        };

        // --- Fonts --------------------------------------------------------------------------

        private void FixFont(CosDictionary font)
        {
            var subtype = (font["Subtype"] as CosName)?.Value;
            if (subtype is "Type3" or null)
            {
                return; // Type 3 glyphs are drawn in the file itself.
            }

            if (subtype == "Type0")
            {
                if (document.Resolve(font["DescendantFonts"]) is CosArray descendants
                    && descendants.Items.Select(document.Resolve).OfType<CosDictionary>().Any(d => !IsEmbedded(d)))
                {
                    Problems.Add(new("The font {0} is not in the file and cannot be added", BaseFont(font)));
                }

                return;
            }

            if (IsEmbedded(font))
            {
                return;
            }

            var name = BaseFont(font);
            var (family, bold, italic) = ParseName(name);
            if (fonts.Find(family, bold, italic) is not { } replacement)
            {
                Problems.Add(new("The font {0} is not in the file and is not installed on this computer", name));
                return;
            }

            EmbedTrueType(font, replacement, italic);
            Changes.Add(new("Embedded an installed font for {0}", name));
        }

        private bool IsEmbedded(CosDictionary font) =>
            document.Resolve(font["FontDescriptor"]) is CosDictionary descriptor
            && (descriptor["FontFile"] is not null || descriptor["FontFile2"] is not null || descriptor["FontFile3"] is not null);

        private static string BaseFont(CosDictionary font) => (font["BaseFont"] as CosName)?.Value ?? "(unnamed)";

        /// <summary>"ABCDEF+Arial,BoldItalic" → (Arial, bold, italic); standard 14 names map to metric-compatible fonts.</summary>
        internal static (string Family, bool Bold, bool Italic) ParseName(string baseFont)
        {
            var name = baseFont;
            var plus = name.IndexOf('+');
            if (plus == 6)
            {
                name = name[(plus + 1)..];
            }

            var parts = name.Split([',', '-'], 2);
            var family = parts[0];
            var style = parts.Length > 1 ? parts[1] : string.Empty;
            if (family.EndsWith("Bold", StringComparison.OrdinalIgnoreCase) && family.Length > 4)
            {
                style += "Bold";
                family = family[..^4];
            }

            var bold = style.Contains("Bold", StringComparison.OrdinalIgnoreCase) || style.Contains("Black", StringComparison.OrdinalIgnoreCase);
            var italic = style.Contains("Italic", StringComparison.OrdinalIgnoreCase) || style.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
            if (family == "Times" && style.Equals("Roman", StringComparison.OrdinalIgnoreCase))
            {
                family = "Times-Roman";
            }

            return (MetricCompatible.TryGetValue(family, out var compatible) ? compatible : family, bold, italic);
        }

        /// <summary>
        /// Turns a simple font that is not in the file into a TrueType font with the installed file embedded:
        /// WinAnsi encoding (standard-encoding quotes kept), widths taken from the embedded font.
        /// </summary>
        private void EmbedTrueType(CosDictionary font, PdfAFont replacement, bool italic)
        {
            var encoding = document.Resolve(font["Encoding"]);
            var codeToChar = new Dictionary<int, char>();
            var cp1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
            for (var code = 32; code <= 255; code++)
            {
                var text = cp1252.GetString([(byte)code]);
                if (text.Length == 1 && text[0] != '?' || code == '?')
                {
                    codeToChar[code] = text[0];
                }
            }

            CosObject newEncoding;
            switch (encoding)
            {
                case CosName { Value: "WinAnsiEncoding" }:
                    newEncoding = encoding;
                    break;
                case CosDictionary dictionary when dictionary["Differences"] is CosArray differences:
                    // Keep the font's own differences; the characters they name decide the widths.
                    var copy = dictionary.Clone();
                    copy["BaseEncoding"] = new CosName("WinAnsiEncoding");
                    newEncoding = copy;
                    var code = 0;
                    foreach (var item in differences.Items)
                    {
                        switch (item)
                        {
                            case CosInteger number:
                                code = (int)number.Value;
                                break;
                            case CosName glyph:
                                if (GlyphNames.ToChar(glyph.Value) is { } c)
                                {
                                    codeToChar[code] = c;
                                }
                                else
                                {
                                    Problems.Add(new("The font {0} uses a character that cannot be matched", BaseFont(font)));
                                }

                                code++;
                                break;
                        }
                    }

                    break;
                default:
                    // No encoding: the standard encoding, which differs from WinAnsi in the two quotes.
                    newEncoding = new CosDictionary()
                        .Set("Type", new CosName("Encoding"))
                        .Set("BaseEncoding", new CosName("WinAnsiEncoding"))
                        .Set("Differences", new CosArray([new CosInteger(39), new CosName("quoteright"), new CosInteger(96), new CosName("quoteleft")]));
                    codeToChar[39] = '’';
                    codeToChar[96] = '‘';
                    break;
            }

            var widths = new CosArray();
            for (var code = 32; code <= 255; code++)
            {
                var width = codeToChar.TryGetValue(code, out var c) ? replacement.AdvanceWidth(c) : null;
                widths.Items.Add(new CosReal(Math.Round(width ?? 0, 2)));
            }

            var data = replacement.Data;
            var fontFile = document.AddObject(new CosStream(
                new CosDictionary().Set("Length1", new CosInteger(data.Length)).Set("Filter", new CosName("FlateDecode")),
                CosFilters.Deflate(data)));
            var flags = 32 | (italic ? 64 : 0); // Nonsymbolic, italic.
            var descriptor = document.AddObject(new CosDictionary()
                .Set("Type", new CosName("FontDescriptor"))
                .Set("FontName", new CosName(replacement.PostScriptName))
                .Set("Flags", new CosInteger(flags))
                .Set("FontBBox", CosArray.FromNumbers(replacement.Box.X0, replacement.Box.Y0, replacement.Box.X1, replacement.Box.Y1))
                .Set("ItalicAngle", new CosReal(replacement.ItalicAngle))
                .Set("Ascent", new CosReal(replacement.Ascent))
                .Set("Descent", new CosReal(replacement.Descent))
                .Set("CapHeight", new CosReal(replacement.CapHeight))
                .Set("StemV", new CosInteger(replacement.Bold ? 140 : 80))
                .Set("FontFile2", fontFile));

            font["Subtype"] = new CosName("TrueType");
            font["BaseFont"] = new CosName(replacement.PostScriptName);
            font["Encoding"] = newEncoding;
            font["FirstChar"] = new CosInteger(32);
            font["LastChar"] = new CosInteger(255);
            font["Widths"] = widths;
            font["FontDescriptor"] = descriptor;
            font.Entries.Remove("ToUnicode"); // WinAnsi already maps to Unicode; a stale map could disagree.
        }

        // --- Output intent and metadata ---------------------------------------------------

        private void AddOutputIntent(CosDictionary catalog)
        {
            var profile = document.AddObject(new CosStream(
                new CosDictionary().Set("N", new CosInteger(3)).Set("Filter", new CosName("FlateDecode")),
                CosFilters.Deflate(SrgbProfile.Bytes)));
            catalog["OutputIntents"] = new CosArray([new CosDictionary()
                .Set("Type", new CosName("OutputIntent"))
                .Set("S", new CosName("GTS_PDFA1"))
                .Set("OutputConditionIdentifier", CosString.FromText(SrgbProfile.Description))
                .Set("Info", CosString.FromText(SrgbProfile.Description))
                .Set("DestOutputProfile", profile)]);
        }

        private void WriteMetadata(CosDictionary catalog, string? title, DateTimeOffset now)
        {
            var info = document.Resolve(document.Trailer["Info"]) as CosDictionary;
            if (info is null)
            {
                info = new CosDictionary();
                document.Trailer["Info"] = document.AddObject(info);
            }

            string? Text(string key) => info[key] is CosString s ? s.ToText().Trim('\0') : null;

            if (string.IsNullOrWhiteSpace(Text("Title")) && !string.IsNullOrWhiteSpace(title))
            {
                info["Title"] = CosString.FromText(title);
            }

            var created = (info["CreationDate"] as CosString)?.ToDate() ?? now;
            created = new DateTimeOffset(created.Year, created.Month, created.Day, created.Hour, created.Minute, created.Second, created.Offset);
            var modified = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, now.Offset);
            info["CreationDate"] = CosString.FromDate(created);
            info["ModDate"] = CosString.FromDate(modified);
            info["Producer"] = CosString.FromText("YRK PDF");
            info.Entries.Remove("Trapped"); // Optional; its XMP twin is easy to get wrong.

            var xmp = new StringBuilder();
            xmp.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
            xmp.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
            xmp.Append("<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" ")
               .Append("xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");
            xmp.Append("<pdfaid:part>2</pdfaid:part>\n<pdfaid:conformance>B</pdfaid:conformance>\n<dc:format>application/pdf</dc:format>\n");
            if (Text("Title") is { Length: > 0 } t)
            {
                xmp.Append("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(Escape(t)).Append("</rdf:li></rdf:Alt></dc:title>\n");
            }

            if (Text("Author") is { Length: > 0 } author)
            {
                xmp.Append("<dc:creator><rdf:Seq><rdf:li>").Append(Escape(author)).Append("</rdf:li></rdf:Seq></dc:creator>\n");
            }

            if (Text("Subject") is { Length: > 0 } subject)
            {
                xmp.Append("<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(Escape(subject)).Append("</rdf:li></rdf:Alt></dc:description>\n");
            }

            if (Text("Keywords") is { Length: > 0 } keywords)
            {
                xmp.Append("<pdf:Keywords>").Append(Escape(keywords)).Append("</pdf:Keywords>\n");
            }

            if (Text("Creator") is { Length: > 0 } creator)
            {
                xmp.Append("<xmp:CreatorTool>").Append(Escape(creator)).Append("</xmp:CreatorTool>\n");
            }

            xmp.Append("<pdf:Producer>YRK PDF</pdf:Producer>\n");
            xmp.Append("<xmp:CreateDate>").Append(XmpDate(created)).Append("</xmp:CreateDate>\n");
            xmp.Append("<xmp:ModifyDate>").Append(XmpDate(modified)).Append("</xmp:ModifyDate>\n");
            xmp.Append("<xmp:MetadataDate>").Append(XmpDate(modified)).Append("</xmp:MetadataDate>\n");
            xmp.Append("</rdf:Description>\n</rdf:RDF>\n</x:xmpmeta>\n");
            xmp.Append(new string(' ', 2000)).Append('\n'); // Padding, so the metadata can be edited in place.
            xmp.Append("<?xpacket end=\"w\"?>");

            catalog["Metadata"] = document.AddObject(new CosStream(
                new CosDictionary().Set("Type", new CosName("Metadata")).Set("Subtype", new CosName("XML")),
                Encoding.UTF8.GetBytes(xmp.ToString())));
        }

        private static string XmpDate(DateTimeOffset date) => date.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

        private static string Escape(string text) => SecurityElement.Escape(new string(text.Where(c => c >= ' ' || c is '\t' or '\n').ToArray()));
    }
}
