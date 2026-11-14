using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Ocr;

/// <summary>Applies edits by original word index, with text filtering decided before any edit.</summary>
public static class OcrReviewEdits
{
    public sealed record Result(OcrPageResult ForPdf, OcrPageResult ForText);

    public static Result Apply(OcrPageResult page, IReadOnlyDictionary<int, string?> edits)
    {
        foreach (var index in edits.Keys)
            if (index < 0 || index >= page.Words.Count) throw new ArgumentOutOfRangeException(nameof(edits));

        // Wrap each occurrence separately: the same record instance may occur at multiple indices.
        var indexed = page.Words.Select(w => w with { }).ToArray();
        var retained = OcrPageText.FilterWords(page with { Words = indexed })
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var pdf = new List<OcrWord>();
        var text = new List<OcrWord>();
        for (var i = 0; i < page.Words.Count; i++)
        {
            var word = page.Words[i];
            if (edits.TryGetValue(i, out var replacement))
            {
                if (string.IsNullOrWhiteSpace(replacement)) continue;
                word = word with { Text = replacement, Confidence = 100 };
            }
            pdf.Add(word);
            if (retained.Contains(indexed[i])) text.Add(word);
        }
        return new(page with { Words = pdf }, page with { Words = text });
    }

    /// <summary>Removes OCR-only layers. Mixed pages stay untouched; unsupported content throws before publication.</summary>
    public static byte[] RemoveExistingPdfLayers(byte[] pdf, IReadOnlyCollection<int> pageIndices)
    {
        if (pageIndices.Count == 0) return pdf;
        var source = CosDocument.Load(pdf);
        var pages = source.GetPages();
        var update = new CosIncrementalUpdate(source);
        var changed = false;
        foreach (var index in pageIndices.Distinct())
        {
            var hidden = false;
            var visible = false;
            OcrScanPages.InspectPage(source, pages[index], isHidden =>
            {
                if (isHidden) hidden = true;
                else visible = true;
            });
            if (!hidden || visible) continue;
            var edit = new CosPageEdit(source, pages[index]);
            var resources = edit.Resources;
            edit.ReplaceContent(Rewrite(edit.ReadContent(), resources, new TextState(), 0));
            edit.Commit(update);
            changed = true;
        }
        return changed ? update.Build().Bytes : pdf;

        byte[] Rewrite(byte[] content, CosDictionary resources, TextState state, int depth)
        {
            if (depth > 20) throw new PdfException("Nested page content cannot be inspected.");
            var stack = new Stack<TextState>();
            var unknownAdvance = false;
            var xobjects = (source.Resolve(resources["XObject"]) as CosDictionary)?.Clone();
            using var output = new MemoryStream();
            var position = 0;
            foreach (var op in ContentTokenizer.Parse(content))
            {
                byte[]? replacement = null;
                switch (op.Operator)
                {
                    case "q": stack.Push(state); break;
                    case "Q": state = stack.Pop(); break;
                    case "BT" or "ET" or "Tm" or "Td" or "TD" or "T*": unknownAdvance = false; break;
                    case "Tr": state = state with { Mode = (int)Number(op.Operands[0]) }; break;
                    case "Tf" when op.Operands[0] is CosName fontName:
                        var fonts = source.Resolve(resources["Font"]) as CosDictionary;
                        state = state with { Font = ContentFont.Load(source, source.Resolve(fonts?[fontName.Value]) as CosDictionary),
                            Size = Number(op.Operands[1]) };
                        break;
                    case "Tc": state = state with { CharSpace = Number(op.Operands[0]) }; break;
                    case "Tw": state = state with { WordSpace = Number(op.Operands[0]) }; break;
                    case "Tj" or "TJ" or "'" or "\"":
                        if (op.Operator is "'" or "\"") unknownAdvance = false;
                        if (op.Operator == "\"") state = state with { WordSpace = Number(op.Operands[0]), CharSpace = Number(op.Operands[1]) };
                        if (state.Mode != 3)
                        {
                            // Unknown fonts in an OCR-only text object are harmless. Never guess the
                            // position of visible text that relies on their character advances, however.
                            if (unknownAdvance) throw new PdfException("The existing OCR layer cannot be replaced safely.");
                            break;
                        }
                        var prefix = op.Operator switch
                        {
                            "'" => "T* ",
                            "\"" => $"{CosWriter.FormatReal(state.WordSpace)} Tw {CosWriter.FormatReal(state.CharSpace)} Tc T* ",
                            _ => string.Empty,
                        };
                        var items = op.Operator == "TJ" && op.Operands[0] is CosArray array ? array.Items : [op.Operands[^1]];
                        var adjustment = 0.0;
                        foreach (var item in items)
                        {
                            if (item is not CosString text) { adjustment += Number(item); continue; }
                            if (state.Font is not { IsSupported: true } font || state.Size == 0)
                            {
                                unknownAdvance = true;
                                continue;
                            }
                            foreach (var code in font.Codes(text.Bytes))
                                adjustment -= font.Width(code) * 1000 + (state.CharSpace
                                    + (font.BytesPerCode == 1 && code == 32 ? state.WordSpace : 0)) * 1000 / state.Size;
                        }
                        // TJ numeric adjustments preserve text advances without retaining old characters.
                        replacement = System.Text.Encoding.ASCII.GetBytes(prefix + "[" + CosWriter.FormatReal(adjustment) + "] TJ\n");
                        break;
                    case "Do" when op.Operands[0] is CosName name && xobjects is not null
                        && source.Resolve(xobjects[name.Value]) is CosStream form
                        && form.Dictionary["Subtype"] is CosName { Value: "Form" }:
                        // Copy resources and use a new invocation name so shared forms/pages remain unchanged.
                        var own = (source.Resolve(form.Dictionary["Resources"]) as CosDictionary ?? resources).Clone();
                        var rewritten = Rewrite(CosFilters.Decode(form, source.Resolve), own, state, depth + 1);
                        var dictionary = form.Dictionary.Clone();
                        dictionary["Resources"] = own;
                        dictionary["DecodeParms"] = null;
                        dictionary["Length"] = null;
                        var suffix = 1;
                        while (xobjects["YrkReview" + suffix] is not null) suffix++;
                        var newName = "YrkReview" + suffix;
                        xobjects[newName] = update.Add(CosPageEdit.Stream(rewritten, dictionary));
                        replacement = System.Text.Encoding.ASCII.GetBytes("/" + newName + " Do\n");
                        break;
                }
                if (replacement is null) continue;
                output.Write(content.AsSpan(position, op.Start - position));
                output.Write(replacement);
                position = op.End;
            }
            output.Write(content.AsSpan(position));
            if (xobjects is not null) resources["XObject"] = xobjects;
            return output.ToArray();
        }

        static double Number(CosObject value) => OcrScanPages.Number(value);
    }

    private sealed record TextState(int Mode = 0, ContentFont? Font = null, double Size = 0, double CharSpace = 0, double WordSpace = 0);
}
