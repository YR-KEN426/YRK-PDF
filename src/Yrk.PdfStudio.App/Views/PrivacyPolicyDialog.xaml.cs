using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace Yrk.PdfStudio.App.Views;

/// <summary>The complete policy, exported from the website and read entirely offline.</summary>
public partial class PrivacyPolicyDialog : Window
{
    public PrivacyPolicyDialog()
    {
        InitializeComponent();
        var language = Loc.IsThai ? Loc.Thai : Loc.English;
        using var stream = typeof(PrivacyPolicyDialog).Assembly.GetManifestResourceStream(
            $"Yrk.PdfStudio.App.Privacy.privacy.{language}.txt")
            ?? throw new InvalidOperationException(Loc.T("Missing embedded privacy policy."));
        using var reader = new StreamReader(stream);
        PolicyViewer.Document = CreateDocument(reader.ReadToEnd(), language);
    }

    private FlowDocument CreateDocument(string text, string language)
    {
        var document = new FlowDocument
        {
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 14,
            Foreground = (Brush)FindResource("TextBrush"),
            Background = (Brush)FindResource("SurfaceBrush"),
            PagePadding = new Thickness(4, 0, 16, 8),
            Language = XmlLanguage.GetLanguage(language),
            Tag = UiTranslator.KeepText
        };
        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length;)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }
            if (line.StartsWith('|'))
            {
                var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 8, 0, 14) };
                var group = new TableRowGroup();
                table.RowGroups.Add(group);
                while (i < lines.Length && lines[i].StartsWith('|'))
                {
                    var cells = lines[i++].Trim('|').Split('|');
                    if (table.Columns.Count == 0)
                    {
                        // Give the longer descriptions and protections most of the width.
                        for (var column = 0; column < cells.Length; column++)
                            table.Columns.Add(new TableColumn { Width = new GridLength(column == 0 ? 1 : 2, GridUnitType.Star) });
                    }
                    var row = new TableRow();
                    var header = group.Rows.Count == 0;
                    foreach (var cell in cells)
                    {
                        row.Cells.Add(new TableCell(ParagraphFor(cell.Trim()))
                        {
                            BorderBrush = (Brush)FindResource("DividerBrush"),
                            BorderThickness = new Thickness(0.5),
                            Padding = new Thickness(8),
                            FontWeight = header ? FontWeights.Bold : FontWeights.Normal,
                            Background = (Brush)FindResource(header ? "SidebarBrush" : "SurfaceBrush")
                        });
                    }
                    group.Rows.Add(row);
                }
                document.Blocks.Add(table);
            }
            else if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                document.Blocks.Add(ReadList(lines, ref i, ListDepth(line)));
            }
            else
            {
                var level = line.StartsWith("## ", StringComparison.Ordinal) ? 2
                    : line.StartsWith("# ", StringComparison.Ordinal) ? 1 : 0;
                var paragraph = ParagraphFor(level == 0 ? line : line[(level + 1)..]);
                paragraph.Margin = new Thickness(0, level == 0 ? 0 : 16, 0, 10);
                if (level != 0)
                {
                    paragraph.FontSize = level == 1 ? 22 : 17;
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.KeepWithNext = true;
                }
                document.Blocks.Add(paragraph);
                i++;
            }
        }
        return document;
    }

    private static int ListDepth(string line) => line.Length - line.TrimStart().Length;

    private static List ReadList(string[] lines, ref int index, int depth)
    {
        var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(22, 0, 0, 0) };
        while (index < lines.Length && lines[index].TrimStart().StartsWith("- ", StringComparison.Ordinal))
        {
            var line = lines[index];
            var nextDepth = ListDepth(line);
            if (nextDepth < depth) break;
            if (nextDepth > depth && list.ListItems.Count > 0)
            {
                list.ListItems.LastListItem.Blocks.Add(ReadList(lines, ref index, nextDepth));
                continue;
            }
            list.ListItems.Add(new ListItem(ParagraphFor(line.TrimStart()[2..])) { Margin = new Thickness(0, 0, 0, 5) });
            index++;
        }
        return list;
    }

    private static Paragraph ParagraphFor(string text)
    {
        var run = new Run { Tag = UiTranslator.KeepText };
        // KeepText protects the viewer, but UiTranslator also visits individual Runs.
        // Bound text is left untouched, preserving the resource's own language.
        run.SetBinding(Run.TextProperty, new Binding { Source = text, Mode = BindingMode.OneWay });
        return new Paragraph(run) { Margin = new Thickness(0) };
    }
}
