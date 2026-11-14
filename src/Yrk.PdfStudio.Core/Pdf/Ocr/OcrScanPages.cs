using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Ocr;

public sealed record OcrScanPage(int PageIndex, bool IsScan, bool HasOcrLayer, bool OcrLayerOnly = false);

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
                InspectPage(source, pages[i], isHidden =>
                {
                    if (isHidden) hidden = true;
                    else visible = true;
                }, cancellationToken);
                understood = true;
            }
            catch (Exception ex) when (ex is PdfException or InvalidOperationException or ArgumentException or NotSupportedException or IOException)
            {
                // Do not classify unrecognised content as an OCR-only page.
            }
            result.Add(new OcrScanPage(i, text.Count(c => !char.IsWhiteSpace(c)) < 20
                || (understood && hidden && !visible), hidden, understood && hidden && !visible));
        }
        return result;
    }

    // Shared preflight: removal must trust exactly the same content that classification understands.
    // Report text as it is encountered so HasOcrLayer survives a later unsupported operation.
    internal static void InspectPage(CosDocument source, CosPage page, Action<bool> reportText,
        CancellationToken cancellationToken = default)
    {
        var edit = new CosPageEdit(source, page);
        var contents = source.Resolve(page.Dictionary["Contents"]);
        if (contents is not null and not CosStream and not CosArray
            || contents is CosArray array && array.Items.Any(item => source.Resolve(item) is not CosStream)
            || page.Resources is not null && source.Resolve(page.Resources) is not CosDictionary)
            throw new PdfException("Unknown page content stream or resources.");
        Inspect(edit.ReadContent(), edit.Resources, 0, 0);

        void Inspect(byte[] content, CosDictionary resources, int mode, int depth)
        {
            if (depth > 20) throw new PdfException("Nested page content cannot be inspected.");
            var states = new Stack<int>();
            var inText = false;
            var marks = 0;
            var operations = ContentTokenizer.Parse(content);
            foreach (var op in operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The shared tokenizer recovers from damaged operands. Verify that it skipped
                // nothing before trusting an OCR-only classification. Unsupported operators,
                // including inline images whose structure we cannot validate here, are rejected.
                ValidateOperands(op);
                var parser = new CosParser(content.AsMemory(op.Start, op.End - op.Start));
                foreach (var _ in op.Operands) parser.ParseObject();
                parser.ExpectKeyword(op.Operator);
                parser.SkipWhitespace();
                if (parser.Position != op.End - op.Start) throw new PdfException("Invalid page content operands.");
                switch (op.Operator)
                {
                    case "BT":
                        if (inText) throw new PdfException("Nested text object.");
                        inText = true; break;
                    case "ET":
                        if (!inText) throw new PdfException("Unbalanced text object.");
                        inText = false; break;
                    case "BMC": case "BDC": marks++; break;
                    case "EMC":
                        if (--marks < 0) throw new PdfException("Unbalanced marked content.");
                        break;
                    case "q": states.Push(mode); break;
                    case "Q":
                        if (states.Count == 0) throw new PdfException("Unbalanced page graphics state.");
                        mode = states.Pop(); break;
                    case "Tr":
                        var value = Number(op.Operands[0]);
                        if (value is < 0 or > 7 || value != Math.Truncate(value))
                            throw new PdfException("Unknown text rendering mode.");
                        mode = (int)value; break;
                    case "Tj": case "TJ": case "'": case "\"":
                        if (!inText) throw new PdfException("Text outside a text object.");
                        var items = op.Operator == "TJ" ? ((CosArray)op.Operands[0]).Items : [op.Operands[^1]];
                        if (items.OfType<CosString>().Any(text => text.Bytes.Length > 0)) reportText(mode == 3);
                        break;
                    case "gs":
                        var stateName = (CosName)op.Operands[0];
                        if (source.Resolve(resources["ExtGState"]) is not CosDictionary statesDictionary
                            || source.Resolve(statesDictionary[stateName.Value]) is not CosDictionary graphicsState
                            || graphicsState["Font"] is not null)
                            throw new PdfException("Unsupported extended graphics state.");
                        break;
                    case "Do":
                        if (op.Operands.Count != 1 || op.Operands[0] is not CosName name
                            || source.Resolve(resources["XObject"]) is not CosDictionary objects
                            || source.Resolve(objects[name.Value]) is not CosStream xobject)
                            throw new PdfException("Unknown page XObject.");
                        if (xobject.Dictionary["Subtype"] is CosName { Value: "Form" })
                        {
                            if (xobject.Dictionary["Resources"] is { } own && source.Resolve(own) is not CosDictionary)
                                throw new PdfException("Unknown form resources.");
                            Inspect(CosFilters.Decode(xobject, source.Resolve),
                                source.Resolve(xobject.Dictionary["Resources"]) as CosDictionary ?? resources, mode, depth + 1);
                        }
                        else if (xobject.Dictionary["Subtype"] is not CosName { Value: "Image" })
                            throw new PdfException("Unknown page XObject type.");
                        break;
                    // Standard graphics, text positioning and marked-content operators do not change Tr.
                    case "Tf": case "Tc": case "Tw": case "Tz": case "TL": case "Ts":
                    case "Td": case "TD": case "Tm": case "T*": case "cm": case "w": case "J": case "j":
                    case "M": case "d": case "ri": case "i": case "m": case "l": case "c":
                    case "v": case "y": case "h": case "re": case "S": case "s": case "f": case "F":
                    case "f*": case "B": case "B*": case "b": case "b*": case "n": case "W": case "W*":
                    case "CS": case "cs": case "SC": case "SCN": case "sc": case "scn": case "G": case "g":
                    case "RG": case "rg": case "K": case "k": case "sh": case "MP": case "DP": break;
                    default: throw new PdfException("Unknown page content operator.");
                }
            }
            var tail = new CosParser(content) { Position = operations.Count == 0 ? 0 : operations[^1].End };
            tail.SkipWhitespace();
            if (tail.Position != content.Length) throw new PdfException("Unfinished page content operation.");
            if (states.Count != 0) throw new PdfException("Unbalanced page graphics state.");
            if (inText || marks != 0) throw new PdfException("Unbalanced page content structure.");
        }
    }

    internal static double Number(CosObject value) => value switch
    {
        CosInteger n => n.Value,
        CosReal n when double.IsFinite(n.Value) => n.Value,
        _ => throw new PdfException("Unsupported numeric operand."),
    };

    private static void ValidateOperands(ContentOperation op)
    {
        var args = op.Operands;
        var signature = op.Operator switch
        {
            "q" or "Q" or "BT" or "ET" or "T*" or "h" or "S" or "s" or "f" or "F" or "f*"
                or "B" or "B*" or "b" or "b*" or "n" or "W" or "W*" or "EMC" => "",
            "Tr" or "Tc" or "Tw" or "Tz" or "TL" or "Ts" or "w" or "J" or "j" or "M" or "i" or "G" or "g" => "n",
            "Td" or "TD" or "m" or "l" => "nn",
            "RG" or "rg" => "nnn",
            "v" or "y" or "re" or "K" or "k" => "nnnn",
            "Tm" or "cm" or "c" => "nnnnnn",
            "Tf" => "Nn",
            "Do" or "gs" or "ri" or "CS" or "cs" or "sh" or "MP" or "BMC" => "N",
            "Tj" or "'" => "s",
            "\"" => "nns",
            "TJ" => "a",
            "d" => "an",
            "DP" or "BDC" => "Np",
            "SC" or "sc" => new string('n', args.Count),
            "SCN" or "scn" => new string('n', args.Count - (args.LastOrDefault() is CosName ? 1 : 0))
                + (args.LastOrDefault() is CosName ? "N" : ""),
            _ => throw new PdfException("Unsupported page content operator."),
        };
        if (args.Count != signature.Length) throw new PdfException("Unsupported operand count.");
        for (var i = 0; i < args.Count; i++)
        {
            var valid = signature[i] switch
            {
                'n' => double.IsFinite(Number(args[i])),
                'N' => args[i] is CosName,
                's' => args[i] is CosString,
                'a' => args[i] is CosArray a && a.Items.All(item => item is CosInteger or CosReal && double.IsFinite(Number(item))
                    || op.Operator == "TJ" && item is CosString),
                'p' => args[i] is CosName or CosDictionary,
                _ => false,
            };
            if (!valid) throw new PdfException("Unsupported page content operand.");
        }
    }
}
