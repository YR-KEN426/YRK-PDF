using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed partial class SavedItemEditingTests
{
    [Fact]
    public async Task Candidate_version_success_commits_pending_items_once_and_preserves_undo_save_and_reopen()
    {
        await using var app = new AppTestHarness();
        var source = await PlacedSource(app);
        var bytes = File.ReadAllBytes(source);
        var baseline = await Render(app);
        var original = (await Items(app))[0];
        var art = Sta.Run(() => StampRenderer.RenderCertifiedCopy(new CertifiedCopyOptions("PENDING-STAMP", false, null)));
        var stamp = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Stamp, 400, 400, art.Image, stamp: art);
        stamp.Bounds = new(.1, .5, .35, .18);
        var picture = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Picture, 400, 400, Image());
        picture.Bounds = new(.5, .5, .1, .1);
        Attach(app, stamp); Attach(app, picture);
        app.Model.AddTextCommand.Execute(null);
        app.Model.OnPageClicked(app.Model.Pages[0], .55, .7);
        var text = Assert.Single(app.Model.Pages[0].Items, i => i.IsText);
        app.Model.CommitText(text, "PENDING-TEXT");
        var pending = (List<PlacedItemViewModel>)typeof(MainViewModel)
            .GetField("_placedItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app.Model)!;
        Assert.Equal(new[] { stamp, picture, text }, pending);
        Assert.False(stamp.FrameCommitted);
        Assert.True(app.Model.UndoCommand.CanExecute(null));

        // Exercise ApplyNowAsync's real successful candidate/write/version/reopen pipeline.
        await Select(app, original);
        app.Model.RotateItem90(Frame(app));
        await EditFinished(app);
        await AssertAppliedAsync();
        Assert.True(app.Model.IsDirty);
        Assert.Equal(bytes, File.ReadAllBytes(source));
        var applied = await Render(app);

        Assert.True(app.Model.UndoCommand.CanExecute(null));
        app.Model.UndoCommand.Execute(null); await Idle(app);
        Assert.Equal(baseline.Pixels, (await Render(app)).Pixels);
        var undone = await Items(app);
        Assert.Equal(2, undone.Count);
        Assert.Equal(original.Bounds, Assert.Single(undone, i => i.Group == original.Group).Bounds);
        Assert.Empty(pending);
        Assert.DoesNotContain(app.Model.Pages[0].Items, i => !i.IsSaved);
        Assert.False(app.Model.UndoCommand.CanExecute(null));
        Assert.True(app.Model.RedoCommand.CanExecute(null));

        app.Model.RedoCommand.Execute(null); await Idle(app);
        await AssertAppliedAsync();
        Assert.Equal(applied.Pixels, (await Render(app)).Pixels);
        var saved = app.PathFor("candidate-success.pdf");
        Assert.True(await app.SaveToAsync(saved));
        // Read the saved file independently: counting overlays alone can hide duplicate PDF content.
        await using (var reopened = await app.Engine.OpenAsync(saved))
        {
            var items = await reopened.GetPageItemsAsync(0);
            Assert.Equal(5, items.Count);
            Assert.All(items, i => Assert.True(i.HasYrkMark));
            Assert.Equal(5, items.Select(i => i.Group).Distinct().Count());
            Assert.Equal(applied.Pixels, (await reopened.RenderPageAsync(0, 400, 400)).Pixels);
        }
        Assert.True(await app.OpenAsync(saved));
        await AssertAppliedAsync();
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Empty(app.Dialogs.Named("ShowError"));

        async Task AssertAppliedAsync()
        {
            Assert.Empty(pending);
            Assert.DoesNotContain(app.Model.Pages[0].Items, i => !i.IsSaved);
            Assert.False(stamp.FrameCommitted);
            var items = await Items(app);
            Assert.Equal(5, items.Count);
            Assert.All(items, i => { Assert.True(i.HasYrkMark); Assert.Null(i.EditError); });
            var rotated = Assert.Single(items, i => i.Group == original.Group);
            Assert.InRange(Math.Abs(rotated.Bounds.Width - original.Bounds.Height), 0, .002);
            Assert.InRange(Math.Abs(rotated.Bounds.Height - original.Bounds.Width), 0, .002);
            Assert.Single(items, i => i.Group == stamp.MarkGroup.ToString("N"));
            Assert.Single(items, i => i.Group == picture.MarkGroup.ToString("N"));
            Assert.Single(items, i => i.Group == text.MarkGroup.ToString("N"));
            Assert.Single(await Document(app).FindTextAsync(0, "PENDING-STAMP", PdfSearchOptions.None));
            Assert.Single(await Document(app).FindTextAsync(0, "PENDING-TEXT", PdfSearchOptions.None));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Candidate_version_write_failure_preserves_pending_items_stamp_frame_and_undo_for_retry(bool failVersionCopy)
    {
        var failed = false;
        await using var app = new AppTestHarness();
        var source = await PlacedSource(app);
        var bytes = File.ReadAllBytes(source);
        var baseline = await Render(app);
        var original = (await Items(app))[0];
        var art = Sta.Run(() => StampRenderer.RenderCertifiedCopy(new CertifiedCopyOptions("PENDING-STAMP", false, null)));
        var stamp = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Stamp, 400, 400, art.Image, stamp: art);
        stamp.Bounds = new(.1, .5, .35, .18);
        var picture = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Picture, 400, 400, Image());
        picture.Bounds = new(.5, .5, .1, .1);
        Attach(app, stamp); Attach(app, picture);
        app.Model.AddTextCommand.Execute(null);
        app.Model.OnPageClicked(app.Model.Pages[0], .55, .7);
        var text = Assert.Single(app.Model.Pages[0].Items, i => i.IsText);
        app.Model.CommitText(text, "PENDING-TEXT");
        Assert.True(app.Model.UndoCommand.CanExecute(null));

        await Select(app, original);
        // ApplyNowAsync writes overlays into a private candidate before saving its version.
        // Wrap that candidate directly: wrapping the engine would reject memory documents
        // before reaching the save pipeline, making this regression a false positive.
        await using (var candidate = await app.Engine.OpenBytesAsync(await Document(app).SaveToBytesAsync(), source))
        {
            await candidate.RotatePageItemAsync((await candidate.GetPageItemsAsync(0))[0].Reference, 90);
            var proxy = DispatchProxy.Create<IPdfDocument, SignatureSecurityTests.TestProxy>();
            ((SignatureSecurityTests.TestProxy)(object)proxy).Handle = (method, args) =>
                method.Name == nameof(IPdfDocument.SaveAsync) ? FailSave(candidate, (string)args![0]!)
                    : method.Invoke(candidate, args);
            var copy = app.PathFor("candidate.pdf");
            await Assert.ThrowsAsync<IOException>(async () =>
            {
                await (Task)typeof(MainViewModel).GetMethod("WriteChangesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(app.Model, [failVersionCopy ? candidate : proxy, copy])!;
                // The exact next step in ApplyNowAsync: copying the written candidate to a version.
                var version = app.PathFor("occupied-version.pdf");
                File.WriteAllText(version, "Existing version");
                failed = true;
                File.Copy(copy, version);
            });
        }

        Assert.True(failed);
        var pending = (List<PlacedItemViewModel>)typeof(MainViewModel)
            .GetField("_placedItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app.Model)!;
        Assert.Multiple(
            () => Assert.Equal(new[] { stamp, picture, text }, pending),
            () => Assert.Contains(picture, app.Model.Pages[0].Items),
            () => Assert.False(stamp.FrameCommitted),
            () => Assert.True(app.Model.UndoCommand.CanExecute(null)));
        Assert.True(app.Model.IsDirty);
        Assert.Equal(baseline.Pixels, (await Render(app)).Pixels);
        Assert.Equal(bytes, File.ReadAllBytes(source));
        app.Model.UndoCommand.Execute(null);
        Assert.DoesNotContain(text, app.Model.Pages[0].Items);
        app.Model.RedoCommand.Execute(null);
        Assert.Contains(text, app.Model.Pages[0].Items);

        await Select(app); // Serializing the original document expired its native item references.
        app.Model.RotateItem90(Frame(app));
        await EditFinished(app);
        Assert.DoesNotContain(app.Model.Pages[0].Items, i => !i.IsSaved);
        var items = await Items(app);
        Assert.Equal(5, items.Count);
        Assert.Single(items, i => i.Group == stamp.MarkGroup.ToString("N") && i.EditError is null);
        Assert.Single(items, i => i.Group == picture.MarkGroup.ToString("N"));
        Assert.NotEmpty(await Document(app).FindTextAsync(0, "PENDING-STAMP", PdfSearchOptions.None));
        Assert.NotEmpty(await Document(app).FindTextAsync(0, "PENDING-TEXT", PdfSearchOptions.None));
        Assert.Empty(app.Dialogs.Named("ShowError"));

        async Task FailSave(IPdfDocument candidate, string path)
        {
            await candidate.SaveAsync(path);
            failed = true;
            throw new IOException("Candidate save failed after writing the PDF");
        }
    }

    [Theory]
    [InlineData(AnnotationTool.EditText)]
    [InlineData(AnnotationTool.Select)]
    public void Saved_text_page_mouse_click_uses_line_editor_only_in_EditText(AnnotationTool tool)
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, typeof(MainWindow).Assembly);
            ResetApplication();
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/YrkEasyPdf;component/Themes/Styles.xaml", UriKind.Relative) });
            var recents = typeof(RecentFiles).GetField("s_items", BindingFlags.NonPublic | BindingFlags.Static)!;
            var oldRecents = recents.GetValue(null);
            recents.SetValue(null, new ObservableCollection<RecentFile>());
            var app = new AppTestHarness();
            MainWindow? window = null;
            try
            {
                var source = app.PathFor("blank.pdf"); File.WriteAllBytes(source, Blank());
                Assert.True(Await(app.OpenAsync(source)));
                app.Model.AddTextCommand.Execute(null);
                app.Model.OnPageClicked(app.Model.Pages[0], .2, .3);
                app.Model.CommitText(Assert.Single(app.Model.Pages[0].Items), "ORIGINAL-LINE");
                var saved = app.PathFor("text.pdf");
                Assert.True(Await(app.SaveToAsync(saved)));
                Assert.True(Await(app.OpenAsync(saved)));
                var item = Assert.Single(Await(Items(app)));
                Assert.Equal(YrkMarkKind.Text, item.Kind);
                var bounds = item.Bounds;
                var point = new NormalizedPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                window = new MainWindow(app.Model, () => throw new NotSupportedException())
                    { Width = 1000, Height = 800, Left = -10000, Top = -10000, ShowActivated = false };
                window.Show();
                ((RadioButton)window.FindName("EditTab")).IsChecked = true;
                app.Model.CurrentTool = tool;
                Settle(window);
                var page = Descendants<Grid>(window).Single(g => ReferenceEquals(g.DataContext, app.Model.Pages[0])
                    && g.Background == Brushes.White && g.ActualWidth > 0);
                // Raise the production page's routed mouse event. Move its visual coordinate system,
                // never the user's pointer, so GetPosition in the real handler hits this saved line.
                var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
                var cursor = click.GetPosition(page);
                page.RenderTransform = new TranslateTransform(cursor.X - point.X * page.ActualWidth,
                    cursor.Y - point.Y * page.ActualHeight);
                page.UpdateLayout();
                var hit = click.GetPosition(page);
                Assert.InRange(Math.Abs(hit.X / page.ActualWidth - point.X), 0, .001);
                Assert.InRange(Math.Abs(hit.Y / page.ActualHeight - point.Y), 0, .001);
                page.RaiseEvent(click);
                PumpUntil(() => app.Model.ActiveTextEdit is not null || app.Model.Pages[0].Items.Any(i => i.IsSaved));
                Assert.True(click.Handled);
                if (tool == AnnotationTool.Select)
                {
                    Assert.Null(app.Model.ActiveTextEdit);
                    Assert.True(Frame(app).IsSelected);
                }
                else
                {
                    var edit = Assert.IsType<TextEditViewModel>(app.Model.ActiveTextEdit);
                    Assert.True(edit.IsEditing);
                    Assert.Equal("ORIGINAL-LINE", edit.Text);
                    Assert.DoesNotContain(app.Model.Pages[0].Items, i => i.IsSaved);
                    app.Model.TextStyleBar.IsAlignedCenter = true;
                    app.Model.EndTextEdit(edit, "REPLACED-LINE", cancel: false);
                    Assert.True(Await(app.SaveToAsync(app.PathFor("edited.pdf"))));
                    Assert.True(Await(app.OpenAsync(app.PathFor("edited.pdf"))));
                    Assert.Empty(Await(Document(app).FindTextAsync(0, "ORIGINAL-LINE", PdfSearchOptions.None)));
                    Assert.NotEmpty(Await(Document(app).FindTextAsync(0, "REPLACED-LINE", PdfSearchOptions.None)));
                }
                Assert.Empty(app.Dialogs.Named("ShowError"));
                return true;
            }
            finally
            {
                window?.Close(); Await(app.DisposeAsync().AsTask());
                recents.SetValue(null, oldRecents);
                application.Shutdown(); ResetApplication();
            }
        });
    }
}
