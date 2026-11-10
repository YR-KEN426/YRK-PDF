using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Cos;

/// <summary>
/// Conservative extraction of top-level painted units. Resources are referenced, never decoded/rebuilt.
/// Clipped or interleaved legacy groups are deliberately not migrated.
/// </summary>
internal static class PlacedContent
{
    internal sealed record Unit(int Start, int End, string Replacement, byte[] Snapshot,
        YrkMark? Mark, bool Grouped, bool Fade, string? XObject, bool Image, string? Error, PdfMatrix Matrix);

    internal static List<Unit> Read(CosDocument document, CosPageEdit edit)
    {
        var bytes = edit.ReadContent();
        var ops = ContentTokenizer.Parse(bytes);
        var result = new List<Unit>();
        var state = new List<string>();
        var stack = new Stack<(List<string> State, bool Clipped, string? Error)>();
        var marks = new List<(string Raw, CosDictionary? Yrk)>();
        var clipped = false;
        string? stateError = null;
        var pathChangedMatrix = false;
        var pathStart = -1;
        string? path = null;
        string Raw(ContentOperation op) => Encoding.Latin1.GetString(bytes, op.Start, op.End - op.Start);
        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            switch (op.Operator)
            {
                case "q":
                    if (pathStart >= 0) pathChangedMatrix = true;
                    stack.Push((state.ToList(), clipped, stateError)); break;
                case "Q":
                    if (stack.Count == 0) throw new PdfException("Unbalanced graphics state in placed-item content.");
                    if (pathStart >= 0) pathChangedMatrix = true;
                    (state, clipped, stateError) = stack.Pop(); break;
                case "BDC":
                case "BMC":
                    CosDictionary? yrk = null;
                    if (op.Operands.FirstOrDefault() is CosName { Value: "YRK" } && op.Operands.Count == 2)
                    {
                        var value = op.Operands[1];
                        if (value is CosName property)
                            value = (document.Resolve(edit.Resources["Properties"]) as CosDictionary)?[property.Value] ?? value;
                        yrk = document.Resolve(value) as CosDictionary;
                    }
                    marks.Add((Raw(op), yrk)); break;
                case "EMC":
                    if (marks.Count == 0) throw new PdfException("Unbalanced marked content in placed-item content.");
                    marks.RemoveAt(marks.Count - 1); break;
                case "W": case "W*": clipped = true; break;
                case "m": case "re":
                    if (pathStart < 0) { pathStart = op.Start; path = ""; }
                    path += Raw(op) + "\n"; break;
                case "l": case "c": case "v": case "y": case "h": path += Raw(op) + "\n"; break;
                case "n": pathStart = -1; path = null; pathChangedMatrix = false; break;
                case "S": case "s": case "f": case "F": case "f*": case "B": case "B*": case "b": case "b*":
                    Add(op.Start, op.End, "n\n", (path ?? "") + Raw(op), null, false,
                        pathChangedMatrix ? "A path changes transformation while being constructed; migration is unsafe." : null);
                    pathStart = -1; path = null; pathChangedMatrix = false; break;
                case "BT":
                    var end = i + 1;
                    while (end < ops.Count && ops[end].Operator != "ET") end++;
                    if (end == ops.Count) throw new PdfException("Unterminated text object in placed-item content.");
                    var text = Encoding.Latin1.GetString(bytes, op.Start, ops[end].End - op.Start);
                    var textOps = ops.Skip(i + 1).Take(end - i - 1).ToArray();
                    Add(op.Start, ops[end].End, "", text, null, false,
                        stack.Count == 0 || !textOps.Any(o => o.Operator == "Tf")
                            || !textOps.Any(o => o.Operator is "Tm" or "Td" or "TD")
                            || textOps.Any(o => o.Operator is "BDC" or "BMC" or "EMC" or "q" or "Q")
                            ? "Legacy text with inherited text state or internal marks cannot be isolated safely." : null);
                    i = end; break;
                case "Do":
                    var name = (op.Operands.FirstOrDefault() as CosName)?.Value;
                    var xobjects = document.Resolve(edit.Resources["XObject"]) as CosDictionary;
                    var stream = document.Resolve(name is null ? null : xobjects?[name]) as CosStream;
                    Add(op.Start, op.End, "", Raw(op), name, (stream?.Dictionary["Subtype"] as CosName)?.Value == "Image");
                    break;
                case "BI": case "sh": Add(op.Start, op.End, "", Raw(op), null, false, "Inline images and shadings cannot be migrated safely."); break;
                case "MP": case "DP": break;
                case "cm":
                    if (op.Operands.Count != 6) throw new PdfException("Invalid transformation in page content.");
                    if (pathStart >= 0) pathChangedMatrix = true;
                    state.Add(Raw(op)); break;
                case "gs":
                    var gsName = (op.Operands.FirstOrDefault() as CosName)?.Value;
                    var states = document.Resolve(edit.Resources["ExtGState"]) as CosDictionary;
                    var gs = document.Resolve(gsName is null ? null : states?[gsName]) as CosDictionary;
                    if (gs is null || gs["SMask"] is not (null or CosName { Value: "None" })
                        || gs["BM"] is not (null or CosName { Value: "Normal" }))
                        stateError = "An external soft mask or blend mode cannot be migrated safely.";
                    state.Add(Raw(op)); break;
                case "w": case "J": case "j": case "M": case "d": case "ri": case "i":
                case "CS": case "cs": case "SC": case "SCN": case "sc": case "scn":
                case "G": case "g": case "RG": case "rg": case "K": case "k":
                    state.Add(Raw(op)); break;
                default:
                    stateError = "Unsupported page graphics state; this item cannot be isolated safely.";
                    state.Add(Raw(op)); break;
            }
        }
        if (stack.Count != 0 || marks.Count != 0) throw new PdfException("Unbalanced page content; placed-item editing is unavailable.");
        return result;

        void Add(int start, int end, string replacement, string paint, string? xobject, bool image, string? error = null)
        {
            var marked = marks.Where(m => m.Yrk is not null).Select(m => m.Yrk!).ToList();
            var dictionary = marked.LastOrDefault();
            string? Value(string key) => (dictionary?[key] as CosString)?.ToText();
            YrkMark? mark = null;
            if (dictionary is not null)
            {
                if (Value("V") != "1" || !Enum.TryParse<YrkMarkKind>(Value("Kind"), out var kind)
                    || !Enum.IsDefined(kind) || !Guid.TryParseExact(Value("Group"), "N", out var group))
                    error = "Unknown or invalid YRK mark; this item cannot be edited safely.";
                else mark = new(kind, group);
                if (marked.Count > 1) error = "Nested YRK marks cannot be migrated safely.";
                if (Value("Format") is not (null or "Group") || Value("Fade") is not (null or "0" or "1"))
                    error = "Unknown YRK group format; this item cannot be edited safely.";
                if (marks.Any(m => m.Yrk is not null && ContentTokenizer.Parse(PlacedGroupBuilder.Bytes(m.Raw))[0].Operands.LastOrDefault() is CosName))
                    error = "Legacy YRK marks stored in property resources cannot be migrated safely.";
            }
            if (clipped) error = "Clipped items cannot be migrated safely.";
            error ??= stateError;
            var snapshot = "q\n" + string.Join("\n", state) + "\n" + string.Join("\n", marks.Select(m => m.Raw))
                + "\n" + paint + "\n" + string.Concat(Enumerable.Repeat("EMC\n", marks.Count)) + "Q\n";
            result.Add(new(start, end, replacement, PlacedGroupBuilder.Bytes(snapshot), mark,
                Value("Format") == "Group", Value("Fade") == "1", xobject, image, error,
                ContentTokenizer.Parse(PlacedGroupBuilder.Bytes(string.Join("\n", state)))
                    .Where(o => o.Operator == "cm").Aggregate(PdfMatrix.Identity, (m, o) => PdfMatrix.FromOperands(o.Operands).Then(m))));
        }
    }

    internal static byte[] Replace(byte[] content, IReadOnlyList<Unit> selected, string invocation, string? stripGroup = null)
    {
        var replacements = selected.Select(u => (u.Start, u.End, Text: u.Replacement + (u == selected[0] ? invocation : ""))).ToList();
        if (stripGroup is not null)
        {
            var marks = new Stack<bool>();
            foreach (var op in ContentTokenizer.Parse(content))
            {
                if (op.Operator is "BDC" or "BMC")
                {
                    var strip = op.Operands.FirstOrDefault() is CosName { Value: "YRK" }
                        && op.Operands.LastOrDefault() is CosDictionary d && (d["Group"] as CosString)?.ToText() == stripGroup;
                    marks.Push(strip);
                    if (strip) replacements.Add((op.Start, op.End, ""));
                }
                else if (op.Operator == "EMC" && marks.TryPop(out var strip) && strip) replacements.Add((op.Start, op.End, ""));
            }
        }
        using var output = new MemoryStream();
        var position = 0;
        foreach (var unit in replacements.OrderBy(u => u.Start))
        {
            output.Write(content.AsSpan(position, unit.Start - position));
            output.Write(PlacedGroupBuilder.Bytes(unit.Text));
            position = unit.End;
        }
        output.Write(content.AsSpan(position));
        return output.ToArray();
    }
}
