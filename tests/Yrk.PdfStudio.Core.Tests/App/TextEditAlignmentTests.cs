using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class TextEditAlignmentTests
{
    private const double PageWidth = 595;
    private const double PageHeight = 842;
    private const double FontSize = 24;
    private const string Original = "ทนายความผู้รับมอบอำนาจ";
    private const string Replacement = "ทนาย";

    [Theory]
    [InlineData(TextAlignment.Center)]
    [InlineData(TextAlignment.Right)]
    public async Task Enter_then_reopen_keeps_original_frame_until_saved(TextAlignment alignment)
    {
        await using var app = new AppTestHarness();
        var edit = await Begin(app, CreateSource(app));
        var original = edit.Line.Bounds;
        Sta.Run(() => { app.Model.EndTextEdit(edit, Replacement, cancel: false, apply: true); return true; });
        await Idle();
        var left = await ReadBounds(Document(app), Replacement);
        Assert.True(left.Width < original.Width / 2);
        await Reopen(left);
        Assert.Equal(original, app.Model.ActiveTextEdit!.AlignmentBounds);
        if (alignment == TextAlignment.Center) app.Model.TextStyleBar.IsAlignedCenter = true;
        else app.Model.TextStyleBar.IsAlignedRight = true;
        Sta.Run(() => { app.Model.EndTextEdit(app.Model.ActiveTextEdit!, Replacement, cancel: false, apply: true); return true; });
        await Idle();
        var aligned = await ReadBounds(Document(app), Replacement);
        var fraction = alignment == TextAlignment.Center ? .5 : 1;
        Assert.InRange(Math.Abs(aligned.X + aligned.Width * fraction - original.X - original.Width * fraction) * PageWidth, 0, 1);
        Assert.True(aligned.X > left.X + .01);
        await Reopen(aligned);
        Assert.Equal(alignment, app.Model.ActiveTextEdit!.Alignment);
        Assert.False(app.Model.ActiveTextEdit.IsChanged);
        app.Model.EndTextEdit(app.Model.ActiveTextEdit, Replacement, cancel: false);
        app.Model.UndoCommand.Execute(null);
        await Idle();
        Assert.InRange(Math.Abs((await ReadBounds(Document(app), Replacement)).X - left.X) * PageWidth, 0, 1);
        app.Model.RedoCommand.Execute(null);
        await Idle();
        await Reopen(await ReadBounds(Document(app), Replacement));
        Assert.Equal(original, app.Model.ActiveTextEdit!.AlignmentBounds);
        app.Model.EndTextEdit(app.Model.ActiveTextEdit, Replacement, cancel: false);
        var output = app.PathFor("repeated.pdf");
        Assert.True(await app.SaveToAsync(output));
        Assert.True(await app.OpenAsync(output));
        var saved = await ReadBounds(Document(app), Replacement);
        await Reopen(saved);
        Assert.Equal(app.Model.ActiveTextEdit!.Line.Bounds, app.Model.ActiveTextEdit.AlignmentBounds);
        Assert.InRange(Math.Abs(saved.X - aligned.X) * PageWidth, 0, 1);
        Assert.Empty(app.Dialogs.Named("ShowError"));

        async Task Reopen(NormalizedRect bounds)
        {
            var task = Sta.Run(() => (Task)typeof(MainViewModel).GetMethod("BeginTextEditAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(app.Model, [app.Model.Pages[0], Center(bounds)])!);
            await task;
        }
        async Task Idle()
        {
            var limit = DateTime.UtcNow.AddSeconds(30);
            while (app.Model.IsBusy)
            {
                Assert.True(DateTime.UtcNow < limit);
                await Task.Delay(10);
            }
        }
    }

    [Theory]
    [InlineData(TextAlignment.Left)]
    [InlineData(TextAlignment.Center)]
    [InlineData(TextAlignment.Right)]
    public async Task Leading_spaces_move_saved_Thai_text_without_polluting_extraction(TextAlignment alignment)
    {
        await using var app = new AppTestHarness();
        var source = CreateSource(app);
        var plain = await ReplaceAndRead(app, source, Replacement, alignment, "plain.pdf");
        var spaced = await ReplaceAndRead(app, source, "   " + Replacement + "  ", alignment, "spaced.pdf");
        var expected = Sta.Run(() => TextShaper.Shape("   ", BundledFonts.Sarabun, false, false, FontSize).Width);

        Assert.True(expected > 1);
        Assert.InRange((spaced.Bounds.X - plain.Bounds.X) * PageWidth, expected - .2, expected + .2);
        Assert.Equal(plain.Bounds.Width, spaced.Bounds.Width, 4);
        Assert.Equal(Replacement, spaced.Text.TrimEnd()); // No leading whitespace after saving and reopening.
    }

    [Theory]
    [InlineData(TextAlignment.Center)]
    [InlineData(TextAlignment.Right)]
    public async Task Shorter_saved_text_aligns_to_the_original_line_within_one_point(TextAlignment alignment)
    {
        await using var app = new AppTestHarness();
        var source = CreateSource(app);
        Assert.True(await app.OpenAsync(source));
        var before = await ReadBounds(Document(app), Original);
        var after = await ReplaceAndRead(app, source, Replacement, alignment, "aligned.pdf");
        var oldAnchor = before.X + before.Width * (alignment == TextAlignment.Center ? .5 : 1);
        var newAnchor = after.Bounds.X + after.Bounds.Width * (alignment == TextAlignment.Center ? .5 : 1);
        Assert.InRange(Math.Abs(newAnchor - oldAnchor) * PageWidth, 0, 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \t\u00a0 ")]
    public async Task Whitespace_only_deletes_the_line_and_text_undo_redo_restores_it(string text)
    {
        await using var app = new AppTestHarness();
        var source = CreateSource(app);
        var edit = await Begin(app, source);
        app.Model.EndTextEdit(edit, text, cancel: false);
        Assert.Equal(string.Empty, edit.Text);
        app.Model.UndoCommand.Execute(null);
        Assert.Equal(Original, edit.Text);
        Assert.False(app.Model.HasTextEdits);
        app.Model.RedoCommand.Execute(null);
        Assert.Equal(string.Empty, edit.Text);
        Assert.True(app.Model.HasTextEdits);

        Assert.True(await app.SaveToAsync(app.PathFor("deleted.pdf")));
        Assert.True(await app.OpenAsync(app.PathFor("deleted.pdf")));
        Assert.True(string.IsNullOrWhiteSpace(await Document(app).GetPageTextAsync(0)));
        Assert.Empty(await Document(app).FindTextAsync(0, Original, PdfSearchOptions.None));
    }

    [Fact]
    public async Task Alignment_only_changes_are_undoable_and_reattach_the_line_for_saving()
    {
        await using var app = new AppTestHarness();
        var edit = await Begin(app, CreateSource(app));
        var bar = app.Model.TextStyleBar;
        Assert.True(bar.IsTextEdit);
        Assert.True(bar.IsAlignedLeft);
        app.Model.EndTextEdit(edit, edit.Text, cancel: false); // Unchanged line is detached.
        Assert.Empty(edit.Page.TextEdits);
        bar.IsAlignedLeft = true;
        Assert.False(app.Model.UndoCommand.CanExecute(null));

        bar.IsAlignedCenter = true;
        Assert.True(edit.IsChanged);
        Assert.True(app.Model.IsDirty);
        Assert.Single(edit.Page.TextEdits);
        bar.IsAlignedRight = true;
        Assert.Equal(TextAlignment.Right, edit.Alignment);
        app.Model.UndoCommand.Execute(null);
        Assert.Equal(TextAlignment.Center, edit.Alignment);
        Assert.True(bar.IsAlignedCenter);
        Assert.False(bar.IsAlignedRight);
        app.Model.UndoCommand.Execute(null);
        Assert.Equal(TextAlignment.Left, edit.Alignment);
        Assert.False(edit.IsChanged);
        Assert.False(app.Model.IsDirty);
        Assert.Empty(edit.Page.TextEdits);
        app.Model.RedoCommand.Execute(null);
        Assert.True(bar.IsAlignedCenter);
        Assert.Single(edit.Page.TextEdits);
        app.Model.RedoCommand.Execute(null);
        Assert.True(bar.IsAlignedRight);
        var expected = Sta.Run(() => MainViewModel.Layout(edit, edit.FontFamilyName).TopLeft.X);

        var output = app.PathFor("alignment-only.pdf");
        Assert.True(await app.SaveToAsync(output));
        Assert.True(await app.OpenAsync(output));
        var bounds = await ReadBounds(Document(app), Original);
        var after = await Document(app).GetTextLineAtAsync(0, Center(bounds));
        Assert.NotNull(after);
        Assert.InRange(Math.Abs(after.BaselineStart.X - expected) * PageWidth, 0, .2);

        bar.SetTarget(null, true); // Added text has no alignment controls or alignment change.
        Assert.False(bar.IsTextEdit);
        var style = app.Model.CurrentTextStyle;
        bar.IsAlignedCenter = true;
        Assert.Equal(style, app.Model.CurrentTextStyle);
        Assert.False(app.Model.IsDirty);
    }

    [Theory]
    [InlineData(TextAlignment.Left, .21)]
    [InlineData(TextAlignment.Center, .15)]
    [InlineData(TextAlignment.Right, .1)]
    public void Long_text_overflows_without_clamping_and_spaces_are_added_after_alignment(TextAlignment alignment, double expected)
    {
        var bounds = new NormalizedRect(.2, .1, .2, .03);
        Assert.Equal(expected, MainViewModel.CalculateTextEditX(bounds, .21, alignment, .3, 0), 10);
        Assert.Equal(expected + .03, MainViewModel.CalculateTextEditX(bounds, .21, alignment, .3, .03), 10);
    }

    [Theory]
    [InlineData(TextAlignment.Left, false)]
    [InlineData(TextAlignment.Center, false)]
    [InlineData(TextAlignment.Right, false)]
    [InlineData(TextAlignment.Left, true)]
    [InlineData(TextAlignment.Center, true)]
    [InlineData(TextAlignment.Right, true)]
    public void Preview_uses_the_same_ink_anchor_and_full_space_indent(TextAlignment alignment, bool editor)
    {
        Sta.Run(() =>
        {
            var line = new PdfTextLine(0, Original, new(.2, .25, .3, .04), new(.2, .28), FontSize,
                new(0, 0, 0), BundledFonts.Sarabun, false, false);
            var edit = new TextEditViewModel(new PageViewModel(0, new(PageWidth, PageHeight)), line, BundledFonts.Sarabun, FontSize)
                { Alignment = alignment };
            var indent = TextShaper.Shape("   ", BundledFonts.Sarabun, false, false, FontSize).Width;
            foreach (var content in new[] { Replacement, string.Concat(Enumerable.Repeat(Original, 3)) })
            {
                var plain = PreviewInk(content);
                var spaced = PreviewInk("   " + content);
                Assert.InRange(spaced.Left - plain.Left, indent - .2, indent + .2);
                var trailing = PreviewInk("   " + content + "  ");
                Assert.InRange(Math.Abs(trailing.Left - spaced.Left), 0, .2);
                if (alignment is TextAlignment.Center or TextAlignment.Right)
                {
                    var fraction = alignment == TextAlignment.Center ? .5 : 1;
                    var expected = (line.Bounds.X + line.Bounds.Width * fraction) * PageWidth;
                    Assert.True(Math.Abs(plain.Left + plain.Width * fraction - expected) <= 1.01,
                        $"{alignment}, editor={editor}, length={content.Length}: {plain.Left + plain.Width * fraction} vs {expected}");
                }
            }

            return true;

            Rect PreviewInk(string text)
            {
                FrameworkElement control = editor
                    ? new TextBox { Text = text, Padding = new Thickness(0), BorderThickness = new Thickness(.5), FontFamily = edit.FontFamily, FontSize = FontSize }
                    : new TextBlock { Text = text, FontFamily = edit.FontFamily, FontSize = FontSize };
                // Give the editor a content host without creating an Application or a visible window.
                if (control is TextBox box)
                {
                    box.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
                        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TextBox">
                            <Border BorderThickness="{TemplateBinding BorderThickness}">
                                <ScrollViewer x:Name="PART_ContentHost" />
                            </Border>
                        </ControlTemplate>
                        """);
                }
                control.MinWidth = edit.Width;
                control.DataContext = edit;
                var alignmentProperty = editor ? TextBox.TextAlignmentProperty : TextBlock.TextAlignmentProperty;
                BindingOperations.SetBinding(control, alignmentProperty, new Binding(nameof(edit.Alignment)));
                var binding = new MultiBinding { Converter = new TextEditPositionConverter(), ConverterParameter = editor ? "Editor" : null };
                binding.Bindings.Add(new Binding());
                binding.Bindings.Add(new Binding("Text") { RelativeSource = RelativeSource.Self });
                binding.Bindings.Add(new Binding("ActualWidth") { RelativeSource = RelativeSource.Self });
                BindingOperations.SetBinding(control, UIElement.RenderTransformProperty, binding);
                control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                control.Arrange(new Rect(control.DesiredSize));
                control.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                control.UpdateLayout();
                var ink = Rect.Empty;
                ReadVisual(control);
                Assert.False(ink.IsEmpty);
                ink.Offset(edit.Left + control.RenderTransform.Value.OffsetX, 0);
                return ink;

                void ReadVisual(Visual visual)
                {
                    if (VisualTreeHelper.GetDrawing(visual) is { } drawing)
                    {
                        var local = ReadDrawing(drawing);
                        if (!local.IsEmpty) ink.Union(visual.TransformToAncestor(control).TransformBounds(local));
                    }
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
                        if (VisualTreeHelper.GetChild(visual, i) is Visual child) ReadVisual(child);
                }

                static Rect ReadDrawing(Drawing drawing)
                {
                    if (drawing is GlyphRunDrawing glyph) return glyph.Bounds;
                    var bounds = Rect.Empty;
                    if (drawing is DrawingGroup group)
                    {
                        foreach (var child in group.Children) bounds.Union(ReadDrawing(child));
                        if (!bounds.IsEmpty && group.Transform is { } transform) bounds = transform.TransformBounds(bounds);
                    }
                    return bounds;
                }
            }
        });
    }

    [Fact]
    public async Task Cancelling_text_keeps_the_previous_leading_spaces()
    {
        await using var app = new AppTestHarness();
        var edit = await Begin(app, CreateSource(app));
        app.Model.EndTextEdit(edit, "   " + Replacement + "  ", cancel: false);
        Assert.Equal("   " + Replacement, edit.Text);
        app.Model.EndTextEdit(edit, "discard this", cancel: true);
        Assert.Equal("   " + Replacement, edit.Text);
        app.Model.UndoCommand.Execute(null);
        Assert.Equal(Original, edit.Text);
        app.Model.RedoCommand.Execute(null);
        Assert.Equal("   " + Replacement, edit.Text);
        // Leave the harness clean without asking to save on disposal.
        app.Model.UndoCommand.Execute(null);
    }

    private static async Task<(NormalizedRect Bounds, string Text)> ReplaceAndRead(
        AppTestHarness app, string source, string text, TextAlignment alignment, string name)
    {
        var edit = await Begin(app, source);
        switch (alignment)
        {
            case TextAlignment.Center: app.Model.TextStyleBar.IsAlignedCenter = true; break;
            case TextAlignment.Right: app.Model.TextStyleBar.IsAlignedRight = true; break;
        }
        app.Model.EndTextEdit(edit, text, cancel: false);
        Assert.Equal(text.TrimEnd(), edit.Text);
        var layout = Sta.Run(() => MainViewModel.Layout(edit, edit.FontFamilyName));
        Assert.Equal(Replacement, string.Concat(layout.Line.Runs.SelectMany(r => r.Glyphs).Select(g => g.Text)));
        var output = app.PathFor(name);
        Assert.True(await app.SaveToAsync(output), string.Join("; ", app.Dialogs.Named("ShowError").Select(c => c.Arguments[0])));
        Assert.True(await app.OpenAsync(output));
        var document = Document(app);
        Assert.Empty(await document.FindTextAsync(0, Original, PdfSearchOptions.None));
        return (await ReadBounds(document, Replacement), await document.GetPageTextAsync(0));
    }

    private static async Task<TextEditViewModel> Begin(AppTestHarness app, string path)
    {
        Assert.True(await app.OpenAsync(path));
        app.Model.CurrentTool = AnnotationTool.EditText;
        var bounds = await ReadBounds(Document(app), Original);
        var task = Sta.Run(() => (Task)typeof(MainViewModel).GetMethod("BeginTextEditAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app.Model, [app.Model.Pages[0], Center(bounds)])!);
        await task;
        var edit = Assert.IsType<TextEditViewModel>(app.Model.ActiveTextEdit);
        Assert.Equal(FontSize, edit.FontSize, 1);
        return edit;
    }

    private static IPdfDocument Document(AppTestHarness app) => (IPdfDocument)typeof(MainViewModel)
        .GetField("_document", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app.Model)!;

    private static NormalizedPoint Center(NormalizedRect bounds) => new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

    private static async Task<NormalizedRect> ReadBounds(IPdfDocument document, string text)
    {
        var rects = Assert.Single(await document.FindTextAsync(0, text, PdfSearchOptions.None)).Rects;
        var x = rects.Min(r => r.X);
        var y = rects.Min(r => r.Y);
        return new(x, y, rects.Max(r => r.X + r.Width) - x, rects.Max(r => r.Y + r.Height) - y);
    }

    internal static string CreateSource(AppTestHarness app, bool unsupported = false)
    {
        var builder = new CosNewDocument();
        var root = builder.Reserve(); var pages = builder.Reserve(); var page = builder.Reserve();
        builder.Set(page, new CosDictionary().Set("Type", new CosName("Page")).Set("Parent", pages)
            .Set("MediaBox", new CosArray([new CosInteger(0), new CosInteger(0), new CosInteger(595), new CosInteger(842)]))
            .Set("Resources", new CosDictionary())
            .Set("Contents", builder.Add(new CosStream(new CosDictionary(), unsupported
                ? "q 595 0 0 842 0 0 cm BI /W 1 /H 1 /BPC 8 /CS /RGB /F /AHx ID 000000> EI Q"u8.ToArray() : []))));
        builder.Set(pages, new CosDictionary().Set("Type", new CosName("Pages")).Set("Kids", new CosArray([page])).Set("Count", new CosInteger(1)));
        builder.Set(root, new CosDictionary().Set("Type", new CosName("Catalog")).Set("Pages", pages));
        var blocks = Sta.Run(() =>
        {
            var shaped = TextShaper.Shape(Original, BundledFonts.Sarabun, false, false, FontSize);
            return TextShaper.ToBlocks([(0, new NormalizedPoint((PageWidth - shaped.Width) / 2 / PageWidth, .25), new PdfColor(0, 0, 0), shaped)]);
        });
        var path = app.PathFor("thai-source.pdf");
        File.WriteAllBytes(path, PdfTextWriter.AddText(builder.Build(root), blocks));
        return path;
    }
}
