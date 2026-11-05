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
internal sealed class ContentRedactor(CosDocument document, CosIncrementalUpdate update, IReadOnlyList<PdfRect> areas, bool securePaths = true)
{
    private const int MaxFormDepth = 12;
    private const double MinOverlap = 0.2;    // Share of a glyph's box inside an area that removes it.
    private const double MinGlyphWidth = 0.3; // Zero-width glyphs (Thai marks) are tested with this width.

    private readonly Dictionary<CosObject, ContentFont> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CosObjectId, CosStream> _newForms = [];

    public sealed record Result(byte[] Content, bool Changed, bool NeedsRaster, Dictionary<string, CosObject> XObjectReplacements,
        Dictionary<string, CosObject> PropertyReplacements);

    public Result Redact(byte[] content, CosDictionary? resources, PdfMatrix ctm, int depth = 0)
    {
        var operations = ContentTokenizer.Parse(content);
        var output = new List<byte[]>(operations.Count);
        var replacements = new Dictionary<string, CosObject>(StringComparer.Ordinal);
        var propertyReplacements = new Dictionary<string, CosObject>(StringComparer.Ordinal);
        var marked = new Stack<MarkedSpan>();
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

        void Touch(PdfRect bounds)
        {
            if (securePaths && Hits(bounds))
                foreach (var span in marked) span.Affected = true;
        }

        void EndSpan()
        {
            if (!marked.TryPop(out var span) || !span.Affected || span.Properties is not { } properties
                || !new[] { "ActualText", "Alt", "E" }.Any(k => properties[k] is not null)) return;
            var cleaned = properties.Clone();
            cleaned["ActualText"] = null;
            cleaned["Alt"] = null;
            cleaned["E"] = null;
            if (span.Name is { } name) propertyReplacements[name] = cleaned;
            else output[span.OutputIndex] = Encoding.Latin1.GetBytes(Serialize([span.Tag!, cleaned]) + " BDC");
            changed = true;
        }

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
                    var painted = op.Operator != "n" && pathPoints.Count > 0;
                    var bounds = PdfRect.Bounds(pathPoints);
                    if (securePaths && painted && op.Operator is "S" or "s" or "B" or "B*" or "b" or "b*")
                    {
                        // The Frobenius norm bounds any CTM scale. Include square/round caps and
                        // the full miter extension; overestimation deliberately chooses raster.
                        var m = state.Ctm;
                        var scale = Math.Sqrt(m.A * m.A + m.B * m.B + m.C * m.C + m.D * m.D);
                        var reach = Math.Abs(state.LineWidth) / 2 * scale
                            * Math.Max(Math.Sqrt(2), state.LineJoin == 0 ? state.MiterLimit : 1);
                        bounds = new PdfRect(bounds.X0 - reach, bounds.Y0 - reach, bounds.X1 + reach, bounds.Y1 + reach);
                        needsRaster |= state.LineWidth == 0; // Device hairlines have no user-space width.
                    }

                    var drop = painted && !pathClips && areas.Any(a => a.Contains(bounds));
                    if (painted) Touch(bounds);
                    needsRaster |= securePaths && painted && Hits(bounds) && !drop;
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
                needsRaster |= securePaths; // Interleaved state changes are not measured by this path buffer.
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
                case "w" when o.Count >= 1:
                    state.LineWidth = Num(o[0]);
                    Keep(op);
                    break;
                case "j" when o.Count >= 1:
                    state.LineJoin = Num(o[0]);
                    Keep(op);
                    break;
                case "M" when o.Count >= 1:
                    state.MiterLimit = Math.Max(1, Num(o[0]));
                    Keep(op);
                    break;
                case "gs":
                    if (securePaths)
                    {
                        var states = document.Resolve(resources?["ExtGState"]) as CosDictionary;
                        var external = o.FirstOrDefault() is CosName name ? document.Resolve(states?[name.Value]) as CosDictionary : null;
                        if (external is null || external.Entries.Keys.Any(k => k is not ("Type" or "LW" or "LC" or "LJ" or "ML" or "D"
                            or "RI" or "OP" or "op" or "OPM" or "CA" or "ca" or "BM" or "AIS" or "TK" or "SA")))
                        {
                            needsRaster = true;
                        }

                        if (external?["LW"] is { } width) state.LineWidth = Num(document.Resolve(width)!);
                        if (external?["LJ"] is { } join) state.LineJoin = Num(document.Resolve(join)!);
                        if (external?["ML"] is { } limit) state.MiterLimit = Math.Max(1, Num(document.Resolve(limit)!));
                    }

                    Keep(op);
                    break;
                case "SCN" or "scn":
                    needsRaster |= securePaths && o.Any(v => v is CosName);
                    Keep(op);
                    break;
                case "sh":
                    // Shadings, patterns, soft masks and external graphics states can paint
                    // beyond the measured path. Preserve their rendered result, then discard them.
                    needsRaster |= securePaths;
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
                    var (rewritten, removed, unmeasurable) = ShowText(items, state, ref tm, Touch);
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
                        Touch(PdfRect.Of(state.Ctm, new PdfRect(0, 0, 1, 1)));
                        needsRaster |= Hits(PdfRect.Of(state.Ctm, new PdfRect(0, 0, 1, 1)));
                    }
                    else if (subtype == "Form" && xobject is not null)
                    {
                        var form = RedactForm(xobject, resources, state.Ctm, depth);
                        if (form.Replacement is not null || form.NeedsRaster)
                            foreach (var span in marked) span.Affected = securePaths;
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
                    Touch(PdfRect.Of(state.Ctm, new PdfRect(0, 0, 1, 1)));
                    needsRaster |= Hits(PdfRect.Of(state.Ctm, new PdfRect(0, 0, 1, 1)));
                    Keep(op);
                    break;

                // --- Marked content: replacement and alternate text could repeat what was removed. ---
                case "BDC":
                    var propertyName = o.Count >= 2 ? o[1] as CosName : null;
                    var named = document.Resolve(resources?["Properties"]) as CosDictionary;
                    var properties = o.Count >= 2 ? document.Resolve(propertyName is null ? o[1] : named?[propertyName.Value]) as CosDictionary : null;
                    marked.Push(new MarkedSpan(output.Count, o.FirstOrDefault(), properties, propertyName?.Value));
                    Keep(op);
                    break;
                case "BMC":
                    marked.Push(new MarkedSpan(output.Count, null, null, null));
                    Keep(op);
                    break;
                case "EMC":
                    EndSpan();
                    Keep(op);
                    break;

                default:
                    // Operators whose painting semantics are not implemented must not silently
                    // leave recoverable content behind. These remaining standard operators only
                    // set state or mark content; the paint operators above do the actual work.
                    needsRaster |= securePaths && op.Operator is not ("ET" or "Tr" or "J" or "d" or "ri" or "i"
                        or "CS" or "cs" or "SC" or "sc" or "G" or "g" or "RG" or "rg" or "K" or "k"
                        or "MP" or "DP" or "BMC" or "BDC" or "EMC" or "BX" or "EX");
                    Keep(op);
                    break;
            }
        }

        if (pathOperations.Count > 0)
        {
            FlushPath(keep: true);
        }

        while (marked.Count > 0) EndSpan();
        var joined = new List<byte>(content.Length);
        foreach (var piece in output)
        {
            joined.AddRange(piece);
            joined.Add((byte)'\n');
        }

        return new Result(joined.ToArray(), changed, needsRaster, replacements, propertyReplacements);
    }

    /// <summary>
    /// Measures each character of a text-showing operator. Returns the TJ array to write instead (removed
    /// characters become position adjustments of the same width), whether anything was removed, and
    /// whether the text could not be measured (and might lie in an area).
    /// </summary>
    private (List<CosObject> Items, bool Removed, bool Unmeasurable) ShowText(IReadOnlyList<CosObject> items, GraphicsState state, ref PdfMatrix tm, Action<PdfRect> touch)
    {
        var font = state.Font;
        var result = new List<CosObject>();
        var removed = false;
        if (!font.IsSupported || state.FontSize == 0)
        {
            // Without metrics the characters' positions are unknown: test a generous box around the run.
            var length = items.OfType<CosString>().Sum(s => s.Bytes.Length);
            var box = PdfRect.Of(tm.Then(state.Ctm), new PdfRect(-1, -2 * Math.Abs(state.FontSize), (length + 2) * Math.Max(1, Math.Abs(state.FontSize)), 2 * Math.Abs(state.FontSize)));
            touch(box);
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
                touch(glyph);
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
        if (securePaths || result.XObjectReplacements.Count > 0 || result.PropertyReplacements.Count > 0)
        {
            var resources = (ownResources ?? parentResources)?.Clone() ?? new CosDictionary();
            if (securePaths)
            {
                try { resources = PdfRedactor.PruneResources(document, result.Content, resources); }
                catch (PdfException) { /* Unknown consumers keep their resources. */ }
            }
            var xobjects = (document.Resolve(resources["XObject"]) as CosDictionary)?.Clone() ?? new CosDictionary();
            foreach (var (name, value) in result.XObjectReplacements)
            {
                xobjects[name] = value;
            }

            resources["XObject"] = xobjects;
            if (result.PropertyReplacements.Count > 0)
            {
                var properties = (document.Resolve(resources["Properties"]) as CosDictionary)?.Clone() ?? new CosDictionary();
                foreach (var (name, value) in result.PropertyReplacements) properties[name] = value;
                resources["Properties"] = properties;
            }
            dictionary["Resources"] = securePaths ? PruneNamedProperties(result.Content, resources) : resources;
        }

        var replacement = CosPageEdit.Stream(result.Content, dictionary);
        var reference = update.Add(replacement);
        _newForms[reference.Id] = replacement;
        return (result.NeedsRaster, reference);
    }

    // A form that gained an isolated resource scope no longer consumes its parent's
    // property dictionary. Do not leave those unused copies of replacement text reachable.
    internal CosDictionary PruneNamedProperties(byte[] content, CosDictionary resources)
    {
        if (document.Resolve(resources["Properties"]) is not CosDictionary properties) return resources;
        if (new[] { "Pattern", "Shading", "ExtGState" }.Any(k => document.Resolve(resources[k]) is CosDictionary { Entries.Count: > 0 }))
            return resources; // Other resource consumers may use named properties too.
        if (document.Resolve(resources["Font"]) is CosDictionary fonts && fonts.Entries.Values.Any(v =>
            document.Resolve(v) is not CosDictionary font || (document.Resolve(font["Subtype"]) as CosName)?.Value == "Type3" && font["Resources"] is null))
            return resources;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<CosStream>(ReferenceEqualityComparer.Instance);
        var xobjects = document.Resolve(resources["XObject"]) as CosDictionary;
        var uncertain = false;
        var budget = 100000;
        void Read(byte[] bytes, int depth)
        {
            if (depth > MaxFormDepth) { uncertain = true; return; }
            foreach (var op in ContentTokenizer.Parse(bytes))
            {
                if (--budget < 0) { uncertain = true; return; }
                if (op.Operator is "BDC" or "DP" && op.Operands.Count >= 2 && op.Operands[1] is CosName name) used.Add(name.Value);
                if (op.Operator != "Do") continue;
                if (op.Operands.FirstOrDefault() is not CosName xname) { uncertain = true; continue; }
                var value = xobjects?[xname.Value];
                var stream = value is CosReference r && _newForms.TryGetValue(r.Id, out var added) ? added : document.Resolve(value) as CosStream;
                if (stream is null) { uncertain = true; continue; }
                if ((document.Resolve(stream.Dictionary["Subtype"]) as CosName)?.Value == "Form"
                    && stream.Dictionary["Resources"] is null && seen.Add(stream)) Read(CosFilters.Decode(stream, document.Resolve), depth + 1);
            }
        }
        try { Read(content, 0); }
        catch (PdfException) { return resources; }
        if (uncertain) return resources;
        var copy = resources.Clone();
        var kept = new CosDictionary();
        foreach (var (name, value) in properties.Entries)
            if (used.Contains(name)) kept[name] = value;
        copy["Properties"] = kept;
        return copy;
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

    private sealed class MarkedSpan(int outputIndex, CosObject? tag, CosDictionary? properties, string? name)
    {
        public int OutputIndex { get; } = outputIndex;
        public CosObject? Tag { get; } = tag;
        public CosDictionary? Properties { get; } = properties;
        public string? Name { get; } = name;
        public bool Affected { get; set; }
    }

    private sealed class GraphicsState
    {
        public PdfMatrix Ctm { get; set; } = PdfMatrix.Identity;

        public double LineWidth { get; set; } = 1;

        public double LineJoin { get; set; }

        public double MiterLimit { get; set; } = 10;

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
