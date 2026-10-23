using System.Globalization;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Redaction;

/// <summary>
/// Rewrites a content stream so nothing inside the redaction areas is left in it: characters there are
/// taken out of their text-showing operators (the remaining characters keep their exact positions),
/// paths entirely inside are removed, form XObjects are redacted recursively, and marked-content
/// replacement text is dropped. Images under an area, and text in fonts that cannot be measured, set
/// <see cref="Result.NeedsRaster"/>: the caller then turns the page into an image.
/// </summary>
internal sealed class ContentRedactor(CosDocument document, CosIncrementalUpdate update, IReadOnlyList<PdfRect> areas)
{
    private const int MaxFormDepth = 12;
    private const double MinOverlap = 0.2;    // Share of a glyph's box inside an area that removes it.
    private const double MinGlyphWidth = 0.3; // Zero-width glyphs (Thai marks) are tested with this width.

    private readonly Dictionary<CosObject, ContentFont> _fonts = new(ReferenceEqualityComparer.Instance);

    public sealed record Result(byte[] Content, bool Changed, bool NeedsRaster, Dictionary<string, CosObject> XObjectReplacements);

    public Result Redact(byte[] content, CosDictionary? resources, PdfMatrix ctm, int depth = 0)
    {
        var operations = ContentTokenizer.Parse(content);
        var output = new List<byte[]>(operations.Count);
        var replacements = new Dictionary<string, CosObject>(StringComparer.Ordinal);
        var changed = false;
        var needsRaster = false;

        var state = new GraphicsState { Ctm = ctm };
        var stack = new Stack<GraphicsState>();
        var tm = PdfMatrix.Identity;
        var tlm = PdfMatrix.Identity;

        var pathOperations = new List<ContentOperation>();
        var pathPoints = new List<(double X, double Y)>();
        var pathClips = false;
        (double X, double Y) current = (0, 0);

        void Keep(ContentOperation operation) => output.Add(content[operation.Start..operation.End]);

        void FlushPath(bool keep)
        {
            if (keep)
            {
                pathOperations.ForEach(Keep);
            }

            pathOperations.Clear();
            pathPoints.Clear();
            pathClips = false;
        }

        foreach (var op in operations)
        {
            var o = op.Operands;
            switch (op.Operator)
            {
                // --- Paths: buffered until painted, then dropped if wholly inside an area. ---
                case "m" or "l" when o.Count >= 2:
                    current = (Num(o[0]), Num(o[1]));
                    pathPoints.Add(state.Ctm.Transform(current.X, current.Y));
                    pathOperations.Add(op);
                    continue;
                case "c" when o.Count >= 6:
                    pathPoints.Add(state.Ctm.Transform(Num(o[0]), Num(o[1])));
                    pathPoints.Add(state.Ctm.Transform(Num(o[2]), Num(o[3])));
                    current = (Num(o[4]), Num(o[5]));
                    pathPoints.Add(state.Ctm.Transform(current.X, current.Y));
                    pathOperations.Add(op);
                    continue;
                case "v" or "y" when o.Count >= 4:
                    pathPoints.Add(state.Ctm.Transform(Num(o[0]), Num(o[1])));
                    current = (Num(o[2]), Num(o[3]));
                    pathPoints.Add(state.Ctm.Transform(current.X, current.Y));
                    pathOperations.Add(op);
                    continue;
                case "re" when o.Count >= 4:
                    double x = Num(o[0]), y = Num(o[1]), w = Num(o[2]), h = Num(o[3]);
                    pathPoints.AddRange([state.Ctm.Transform(x, y), state.Ctm.Transform(x + w, y), state.Ctm.Transform(x, y + h), state.Ctm.Transform(x + w, y + h)]);
                    pathOperations.Add(op);
                    continue;
                case "h":
                    pathOperations.Add(op);
                    continue;
                case "W" or "W*":
                    pathClips = true;
                    pathOperations.Add(op);
                    continue;
                case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "n":
                    var drop = op.Operator != "n" && !pathClips && pathPoints.Count > 0
                        && areas.Any(a => a.Contains(PdfRect.Bounds(pathPoints)));
                    FlushPath(keep: !drop);
                    if (drop)
                    {
                        changed = true;
                    }
                    else
                    {
                        Keep(op);
                    }

                    continue;
            }

            if (pathOperations.Count > 0)
            {
                FlushPath(keep: true); // A path left unpainted (invalid, but seen in the wild).
            }

            switch (op.Operator)
            {
                case "q":
                    stack.Push(state.Clone());
                    Keep(op);
                    break;
                case "Q":
                    if (stack.Count > 0)
                    {
                        state = stack.Pop();
                    }

                    Keep(op);
                    break;
                case "cm" when o.Count >= 6:
                    state.Ctm = PdfMatrix.FromOperands(o).Then(state.Ctm);
                    Keep(op);
                    break;

                // --- Text state ---
                case "BT":
                    tm = tlm = PdfMatrix.Identity;
                    Keep(op);
                    break;
                case "Tc" when o.Count >= 1:
                    state.CharSpacing = Num(o[0]);
                    Keep(op);
                    break;
                case "Tw" when o.Count >= 1:
                    state.WordSpacing = Num(o[0]);
                    Keep(op);
                    break;
                case "Tz" when o.Count >= 1:
                    state.HorizontalScale = Num(o[0]) / 100;
                    Keep(op);
                    break;
                case "TL" when o.Count >= 1:
                    state.Leading = Num(o[0]);
                    Keep(op);
                    break;
                case "Ts" when o.Count >= 1:
                    state.Rise = Num(o[0]);
                    Keep(op);
                    break;
                case "Tf" when o.Count >= 2:
                    state.Font = o[0] is CosName fontName ? Font(resources, fontName.Value) : ContentFont.Unsupported;
                    state.FontSize = Num(o[1]);
                    Keep(op);
                    break;
                case "Td" or "TD" when o.Count >= 2:
                    if (op.Operator == "TD")
                    {
                        state.Leading = -Num(o[1]);
                    }

                    tlm = new PdfMatrix(1, 0, 0, 1, Num(o[0]), Num(o[1])).Then(tlm);
                    tm = tlm;
                    Keep(op);
                    break;
                case "Tm" when o.Count >= 6:
                    tm = tlm = PdfMatrix.FromOperands(o);
                    Keep(op);
                    break;
                case "T*":
                    tlm = new PdfMatrix(1, 0, 0, 1, 0, -state.Leading).Then(tlm);
                    tm = tlm;
                    Keep(op);
                    break;

                // --- Text showing ---
                case "Tj" or "TJ" or "'" or "\"":
                    var prefix = string.Empty;
                    if (op.Operator is "'" or "\"")
                    {
                        if (op.Operator == "\"" && o.Count >= 3)
                        {
                            state.WordSpacing = Num(o[0]);
                            state.CharSpacing = Num(o[1]);
                            prefix = $"{F(state.WordSpacing)} Tw {F(state.CharSpacing)} Tc ";
                        }

                        tlm = new PdfMatrix(1, 0, 0, 1, 0, -state.Leading).Then(tlm);
                        tm = tlm;
                        prefix += "T*\n";
                    }

                    var items = op.Operator == "TJ" && o.Count >= 1 && o[0] is CosArray array
                        ? array.Items
                        : o.Count >= 1 ? [o[^1]] : [];
                    var (rewritten, removed, unmeasurable) = ShowText(items, state, ref tm);
                    needsRaster |= unmeasurable;
                    if (removed)
                    {
                        changed = true;
                        output.Add(Encoding.Latin1.GetBytes(prefix + Serialize(rewritten, asArray: true) + " TJ"));
                    }
                    else
                    {
                        Keep(op);
                    }

                    break;

                // --- XObjects and inline images ---
                case "Do" when o.Count >= 1 && o[0] is CosName xobjectName:
                    var xobjects = document.Resolve(resources?["XObject"]) as CosDictionary;
                    var xobject = document.Resolve(xobjects?[xobjectName.Value]) as CosStream;
                    var subtype = (document.Resolve(xobject?.Dictionary["Subtype"]) as CosName)?.Value;
                    if (subtype == "Image")
                    {
                        needsRaster |= Hits(PdfRect.Of(state.Ctm, new PdfRect(0, 0, 1, 1)));
                    }
                    else if (subtype == "Form" && xobject is not null)
                    {
                        var form = RedactForm(xobject, resources, state.Ctm, depth);
                        needsRaster |= form.NeedsRaster;
                        if (form.Replacement is not null)
                        {
                            replacements[xobjectName.Value] = form.Replacement;
                            changed = true;
                        }
                    }

                    Keep(op);
                    break;
                case "BI":
                    needsRaster |= Hits(PdfRect.Of(state.Ctm, new PdfRect(0, 0, 1, 1)));
                    Keep(op);
                    break;

                // --- Marked content: replacement and alternate text could repeat what was removed. ---
                case "BDC" when o.Count >= 2 && o[1] is CosDictionary properties
                                && (properties["ActualText"] is not null || properties["Alt"] is not null || properties["E"] is not null):
                    var cleaned = properties.Clone();
                    cleaned["ActualText"] = null;
                    cleaned["Alt"] = null;
                    cleaned["E"] = null;
                    output.Add(Encoding.Latin1.GetBytes(Serialize([o[0], cleaned]) + " BDC"));
                    changed = true;
                    break;

                default:
                    Keep(op);
                    break;
            }
        }

        if (pathOperations.Count > 0)
        {
            FlushPath(keep: true);
        }

        var joined = new List<byte>(content.Length);
        foreach (var piece in output)
        {
            joined.AddRange(piece);
            joined.Add((byte)'\n');
        }

        return new Result(joined.ToArray(), changed, needsRaster, replacements);
    }

    /// <summary>
    /// Measures each character of a text-showing operator. Returns the TJ array to write instead (removed
    /// characters become position adjustments of the same width), whether anything was removed, and
    /// whether the text could not be measured (and might lie in an area).
    /// </summary>
    private (List<CosObject> Items, bool Removed, bool Unmeasurable) ShowText(IReadOnlyList<CosObject> items, GraphicsState state, ref PdfMatrix tm)
    {
        var font = state.Font;
        var result = new List<CosObject>();
        var removed = false;
        if (!font.IsSupported || state.FontSize == 0)
        {
            // Without metrics the characters' positions are unknown: test a generous box around the run.
            var length = items.OfType<CosString>().Sum(s => s.Bytes.Length);
            var box = PdfRect.Of(tm.Then(state.Ctm), new PdfRect(-1, -2 * Math.Abs(state.FontSize), (length + 2) * Math.Max(1, Math.Abs(state.FontSize)), 2 * Math.Abs(state.FontSize)));
            return (result, false, Hits(box) || state.FontSize == 0);
        }

        var size = state.FontSize;
        var scale = state.HorizontalScale;
        var kept = new List<byte>();

        void FlushKept()
        {
            if (kept.Count > 0)
            {
                result.Add(new CosString(kept.ToArray(), isHex: true));
                kept.Clear();
            }
        }

        void AddAdjustment(double value)
        {
            if (result.Count > 0 && kept.Count == 0 && result[^1] is CosReal previous)
            {
                result[^1] = new CosReal(previous.Value + value);
            }
            else
            {
                FlushKept();
                result.Add(new CosReal(value));
            }
        }

        foreach (var item in items)
        {
            if (item is CosInteger or CosReal)
            {
                var adjustment = PdfMatrix.Number(item);
                tm = new PdfMatrix(1, 0, 0, 1, -adjustment / 1000 * size * scale, 0).Then(tm);
                AddAdjustment(adjustment);
                continue;
            }

            if (item is not CosString text)
            {
                continue;
            }

            var bytes = text.Bytes;
            var codeIndex = 0;
            foreach (var code in font.Codes(bytes))
            {
                var width = font.Width(code);
                var spacing = state.CharSpacing + (font.BytesPerCode == 1 && code == 32 ? state.WordSpacing : 0);
                var advance = ((width * size) + spacing) * scale;

                // The glyph's box: its advance (at least a minimum, for zero-width marks) by ascent and descent.
                var trm = new PdfMatrix(size * scale, 0, 0, size, 0, state.Rise).Then(tm).Then(state.Ctm);
                var glyph = PdfRect.Of(trm, new PdfRect(0, font.Descent, Math.Max(width, MinGlyphWidth), font.Ascent));
                if (areas.Any(a => a.OverlapArea(glyph) >= MinOverlap * glyph.Area && glyph.Area > 0))
                {
                    removed = true;
                    AddAdjustment(-advance * 1000 / (size * scale));
                }
                else
                {
                    kept.AddRange(bytes.AsSpan(codeIndex * font.BytesPerCode, font.BytesPerCode).ToArray());
                }

                tm = new PdfMatrix(1, 0, 0, 1, advance, 0).Then(tm);
                codeIndex++;
            }
        }

        FlushKept();
        return (result, removed, false);
    }

    private (bool NeedsRaster, CosObject? Replacement) RedactForm(CosStream form, CosDictionary? parentResources, PdfMatrix ctm, int depth)
    {
        var matrix = PdfMatrix.FromArray(document.Resolve(form.Dictionary["Matrix"]) as CosArray).Then(ctm);
        var bbox = document.ToRect(form.Dictionary["BBox"]);
        if (bbox is { } b && !Hits(PdfRect.Of(matrix, new PdfRect(b.X0, b.Y0, b.X1, b.Y1))))
        {
            return (false, null);
        }

        if (depth >= MaxFormDepth)
        {
            return (true, null);
        }

        var ownResources = document.Resolve(form.Dictionary["Resources"]) as CosDictionary;
        var result = Redact(CosFilters.Decode(form, document.Resolve), ownResources ?? parentResources, matrix, depth + 1);
        if (!result.Changed)
        {
            return (result.NeedsRaster, null);
        }

        // A changed copy; the original may be shared with pages or forms that keep it as it is.
        var dictionary = form.Dictionary.Clone();
        dictionary["Filter"] = null;
        dictionary["DecodeParms"] = null;
        dictionary["Length"] = null;
        if (result.XObjectReplacements.Count > 0)
        {
            var resources = (ownResources ?? parentResources)?.Clone() ?? new CosDictionary();
            var xobjects = (document.Resolve(resources["XObject"]) as CosDictionary)?.Clone() ?? new CosDictionary();
            foreach (var (name, value) in result.XObjectReplacements)
            {
                xobjects[name] = value;
            }

            resources["XObject"] = xobjects;
            dictionary["Resources"] = resources;
        }

        return (result.NeedsRaster, update.Add(CosPageEdit.Stream(result.Content, dictionary)));
    }

    private ContentFont Font(CosDictionary? resources, string name)
    {
        var fonts = document.Resolve(resources?["Font"]) as CosDictionary;
        var key = fonts?[name];
        if (key is null)
        {
            return ContentFont.Unsupported;
        }

        if (!_fonts.TryGetValue(key, out var font))
        {
            try
            {
                font = ContentFont.Load(document, document.Resolve(key) as CosDictionary);
            }
            catch (PdfException)
            {
                font = ContentFont.Unsupported;
            }

            _fonts[key] = font;
        }

        return font;
    }

    private bool Hits(PdfRect box) => areas.Any(a => a.Intersects(box));

    private static double Num(CosObject value) => PdfMatrix.Number(value);

    private static string F(double value) => CosWriter.FormatReal(value);

    /// <summary>Writes objects separated by spaces (operands), or as one array when <paramref name="asArray"/> is set.</summary>
    private static string Serialize(IReadOnlyList<CosObject> items, bool asArray = false)
    {
        using var stream = new MemoryStream();
        var writer = new CosWriter(stream);
        writer.Write(new CosArray(items));
        var text = Encoding.Latin1.GetString(stream.ToArray());
        return asArray ? text : text[1..^1];
    }

    private sealed class GraphicsState
    {
        public PdfMatrix Ctm { get; set; } = PdfMatrix.Identity;

        public double CharSpacing { get; set; }

        public double WordSpacing { get; set; }

        public double HorizontalScale { get; set; } = 1;

        public double Leading { get; set; }

        public double Rise { get; set; }

        public ContentFont Font { get; set; } = ContentFont.Unsupported;

        public double FontSize { get; set; }

        public GraphicsState Clone() => (GraphicsState)MemberwiseClone();
    }
}
