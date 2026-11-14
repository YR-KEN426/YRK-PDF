using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Ocr;

public sealed record OcrScanPage(int PageIndex, bool IsScan, bool HasOcrLayer);

/// <summary>Distinguishes real page text from an invisible OCR layer. Unknown content is conservative.</summary>
public static class OcrScanPages
{
    public static async Task<IReadOnlyList<OcrScanPage>> FindAsync(IPdfDocument document, byte[] pdf,
        CancellationToken cancellationToken = default)
    {
        var source = CosDocument.Load(pdf);
        var pages = source.GetPages();
        var result = new List<OcrScanPage>();
        for (var i = 0; i < document.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await document.GetPageTextAsync(i, cancellationToken).ConfigureAwait(false);
            var hidden = false;
            var visible = false;
            var understood = false;
            try
            {
                var edit = new CosPageEdit(source, pages[i]);
                // ReadContent tolerates invalid array entries for editing; classification must not.
                var contents = source.Resolve(pages[i].Dictionary["Contents"]);
                if (contents is not null and not CosStream and not CosArray
                    || contents is CosArray array && array.Items.Any(item => source.Resolve(item) is not CosStream))
                    throw new PdfException("Unknown page content stream.");
                Inspect(edit.ReadContent(), edit.Resources, 0, 0);
                understood = true;
            }
            catch (Exception ex) when (ex is PdfException or InvalidOperationException or ArgumentException or NotSupportedException or IOException)
            {
                // Do not classify unrecognised content as an OCR-only page.
            }
            result.Add(new OcrScanPage(i, text.Count(c => !char.IsWhiteSpace(c)) < 20
                || (understood && hidden && !visible), hidden));

            void Inspect(byte[] content, CosDictionary resources, int mode, int depth)
            {
                if (depth > 20) throw new PdfException("Nested page content cannot be inspected.");
                var states = new Stack<int>();
                var operations = ContentTokenizer.Parse(content);
                foreach (var op in operations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // The shared tokenizer recovers from damaged operands. Verify that it skipped
                    // nothing before trusting an OCR-only classification (inline image bytes excepted).
                    if (op.Operator != "BI")
                    {
                        var parser = new CosParser(content.AsMemory(op.Start, op.End - op.Start));
                        foreach (var _ in op.Operands) parser.ParseObject();
                        parser.ExpectKeyword(op.Operator);
                        parser.SkipWhitespace();
                        if (parser.Position != op.End - op.Start) throw new PdfException("Invalid page content operands.");
                    }
                    switch (op.Operator)
                    {
                        case "q": states.Push(mode); break;
                        case "Q":
                            if (states.Count == 0) throw new PdfException("Unbalanced page graphics state.");
                            mode = states.Pop(); break;
                        case "Tr":
                            if (op.Operands.Count != 1 || op.Operands[0] is not CosInteger value || value.Value is < 0 or > 7)
                                throw new PdfException("Unknown text rendering mode.");
                            mode = (int)value.Value; break;
                        case "Tj": case "TJ": case "'": case "\"":
                            if (mode == 3) hidden = true;
                            else visible = true;
                            break;
                        case "Do":
                            if (op.Operands.Count != 1 || op.Operands[0] is not CosName name
                                || source.Resolve(resources["XObject"]) is not CosDictionary objects
                                || source.Resolve(objects[name.Value]) is not CosStream xobject)
                                throw new PdfException("Unknown page XObject.");
                            if (xobject.Dictionary["Subtype"] is CosName { Value: "Form" })
                                Inspect(CosFilters.Decode(xobject, source.Resolve),
                                    source.Resolve(xobject.Dictionary["Resources"]) as CosDictionary ?? resources, mode, depth + 1);
                            else if (xobject.Dictionary["Subtype"] is not CosName { Value: "Image" })
                                throw new PdfException("Unknown page XObject type.");
                            break;
                        // Standard graphics, text positioning and marked-content operators do not change Tr.
                        case "BT": case "ET": case "Tf": case "Tc": case "Tw": case "Tz": case "TL": case "Ts":
                        case "Td": case "TD": case "Tm": case "T*": case "cm": case "w": case "J": case "j":
                        case "M": case "d": case "ri": case "i": case "gs": case "m": case "l": case "c":
                        case "v": case "y": case "h": case "re": case "S": case "s": case "f": case "F":
                        case "f*": case "B": case "B*": case "b": case "b*": case "n": case "W": case "W*":
                        case "CS": case "cs": case "SC": case "SCN": case "sc": case "scn": case "G": case "g":
                        case "RG": case "rg": case "K": case "k": case "sh": case "BI": case "MP": case "DP":
                        case "BMC": case "BDC": case "EMC": case "BX": case "EX": break;
                        default: throw new PdfException("Unknown page content operator.");
                    }
                }
                var tail = new CosParser(content) { Position = operations.Count == 0 ? 0 : operations[^1].End };
                tail.SkipWhitespace();
                if (tail.Position != content.Length) throw new PdfException("Unfinished page content operation.");
                if (states.Count != 0) throw new PdfException("Unbalanced page graphics state.");
            }
        }
        return result;
    }
}
