using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.Core.Tests;

public sealed partial class SavedItemEditingTests
{
    [Theory]
    [InlineData("save")]
    [InlineData("replace")]
    [InlineData("transform")]
    public void Reopen_keeps_rendered_view_and_cancels_old_page_work(string operation)
    {
        WithHomeWindow((app, home, _, _) =>
        {
            var window = Assert.IsType<MainWindow>(Window.GetWindow(home));
            var content = Assert.IsType<Grid>(Assert.IsType<Grid>(window.Content).Children[0]);
            Assert.Equal(nameof(MainViewModel.IsBusy), BindingOperations.GetBinding(content, UIElement.IsEnabledProperty)!.Path.Path);
            var ribbon = content.Children.OfType<Border>().Single(b => Grid.GetRow(b) == 1);
            Assert.Equal(nameof(MainViewModel.ShowsDocument), BindingOperations.GetBinding(ribbon, UIElement.VisibilityProperty)!.Path.Path);
            var documentArea = content.Children.OfType<Grid>().Single(g => Grid.GetRow(g) == 2);
            var sidebar = documentArea.Children.OfType<Border>().Single(b => Grid.GetColumn(b) == 0);
            Assert.Contains(BindingOperations.GetMultiBinding(sidebar, UIElement.VisibilityProperty)!.Bindings.Cast<Binding>(),
                b => b.Path.Path == nameof(MainViewModel.ShowsDocument));
            var source = app.CreatePdf();
            Assert.True(Await(app.OpenAsync(source)));
            var pages = app.Model.Pages;
            var page = pages[0];
            PumpUntil(() => page.Image is not null);
            app.Model.RequestThumbnail(page);
            PumpUntil(() => page.Thumbnail is not null);
            var image = page.Image;
            var thumbnail = page.Thumbnail;
            var detailImage = Image();
            typeof(PageViewModel).GetMethod("SetDetail", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(page, [detailImage, new Rect(0, 0, 100, 100)]);
            var bookmarks = new[] { BookmarkViewModel.From(new PdfBookmark("Chapter", 0, [])) };
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.Bookmarks))!.SetValue(app.Model, bookmarks);
            using var render = new CancellationTokenSource();
            using var thumbnailWork = new CancellationTokenSource();
            CancellationTokenSource? detail = null;
            var observed = false;
            void Record(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(MainViewModel.IsReopeningDocument) && app.Model.IsReopeningDocument)
                {
                    typeof(PageViewModel).GetMethod("BeginRender", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(page, [render, 999]);
                    detail = (CancellationTokenSource)typeof(PageViewModel).GetMethod("BeginDetail", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(page, null)!;
                    typeof(PageViewModel).GetProperty("PendingThumbnail", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(page, thumbnailWork);
                }
                if (observed || app.Model.HasDocument || !app.Model.IsReopeningDocument) return;
                observed = true;
                Assert.Same(pages, app.Model.Pages);
                Assert.Same(bookmarks, app.Model.Bookmarks);
                Assert.Same(image, page.Image);
                Assert.Same(thumbnail, page.Thumbnail);
                Assert.Same(detailImage, page.DetailImage);
                Assert.True(render.IsCancellationRequested);
                Assert.True(thumbnailWork.IsCancellationRequested);
                Assert.True(detail!.IsCancellationRequested);
                Assert.True(app.Model.ShowsDocument);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("TabStripBar")).Visibility);
                Assert.Contains(Path.GetFileName(source), app.Model.WindowTitle);
                Assert.Equal(operation != "replace", app.Model.IsBusy);
                // UI callbacks can arrive after release. None may start native work on old pages.
                app.Model.UpdateViewport(0, 800, 600);
                app.Model.RequestThumbnail(page);
                app.Model.UpdateDetail(page, new Rect(0, 0, 100, 100));
                app.Model.ReleaseThumbnail(page);
                Assert.Same(image, page.Image);
                Assert.Same(thumbnail, page.Thumbnail);
            }
            app.Model.PropertyChanged += Record;
            try
            {
                if (operation == "save") Assert.True(Await(app.SaveToAsync(source)));
                else if (operation == "replace") Assert.True(Await(app.OpenAsync(source)));
                else
                {
                    Func<IPdfDocument, byte[], CancellationToken, Task<byte[]?>> transform = (_, bytes, _) => Task.FromResult<byte[]?>(bytes);
                    var task = (Task<string?>)typeof(MainViewModel).GetMethod("TransformToFileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(app.Model, [source, "Testing", transform])!;
                    Assert.Equal(source, Await(task));
                }
            }
            finally
            {
                app.Model.PropertyChanged -= Record;
                detail?.Dispose();
            }
            Assert.True(observed);
            Assert.NotSame(pages, app.Model.Pages);
            Assert.NotSame(bookmarks, app.Model.Bookmarks);
            // Delayed callbacks for recycled controls must not render old pages against the new document.
            app.Model.ReleaseThumbnail(page);
            app.Model.RequestThumbnail(page);
            typeof(PageViewModel).GetMethod("SetDisplaySize", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(page, [8000d, 8000d]);
            app.Model.UpdateDetail(page, new Rect(0, 0, 100, 100));
            Assert.Null(typeof(PageViewModel).GetProperty("PendingThumbnail", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
            Assert.Null(typeof(PageViewModel).GetField("_pendingDetail", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
            Assert.Empty(app.Dialogs.Named("ShowError"));
        });
    }

    [Fact]
    public void Close_command_clears_document_view_when_not_reopening()
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var app = new AppTestHarness();
            try
            {
                Assert.True(Await(app.OpenAsync(app.CreatePdf())));
                Assert.NotEmpty(app.Model.Pages);
                Await(app.Model.CloseCommand.ExecuteAsync(null));
                Assert.False(app.Model.HasDocument);
                Assert.False(app.Model.ShowsDocument);
                Assert.Empty(app.Model.Pages);
                Assert.Empty(app.Model.Bookmarks);
                Assert.Equal("YRK PDF", app.Model.WindowTitle);
            }
            finally { Await(app.DisposeAsync().AsTask()); }
            return true;
        });
    }

    [Theory]
    [InlineData("text")]
    [InlineData("enter")]
    [InlineData("overlay")]
    [InlineData("version")]
    public void Home_stays_hidden_at_every_binding_notification_during_save_and_version_reopen(string edit)
    {
        WithHomeWindow((app, home, isHomeVisible, bindingProperties) =>
        {
            var source = TextEditAlignmentTests.CreateSource(app);
            Assert.True(Await(app.OpenAsync(source)));
            var samples = new List<(string? Property, bool Visible)>();
            var displaySamples = new List<(bool Shows, bool Reopening, bool HasDocument, int Pages, string Title, string Tab)>();
            bindingProperties.UnionWith([nameof(MainViewModel.ShowsDocument), nameof(MainViewModel.WindowTitle),
                nameof(MainViewModel.Pages), nameof(MainViewModel.Bookmarks), nameof(MainViewModel.TabTitle),
                nameof(MainViewModel.IsReopeningDocument)]);
            void Record(object? sender, PropertyChangedEventArgs e)
            {
                if (string.IsNullOrEmpty(e.PropertyName) || bindingProperties.Contains(e.PropertyName))
                {
                    samples.Add((e.PropertyName, isHomeVisible()));
                    displaySamples.Add((app.Model.ShowsDocument, app.Model.IsReopeningDocument,
                        app.Model.HasDocument, app.Model.Pages.Count, app.Model.WindowTitle, app.Model.TabTitle));
                }
            }
            app.Model.PropertyChanged += Record;
            try
            {
                if (edit is "text" or "enter")
                {
                    var original = Await(Document(app).GetPageTextAsync(0)).Trim();
                    var bounds = Assert.Single(Await(Document(app).FindTextAsync(0, original, PdfSearchOptions.None))).Rects[0];
                    app.Model.CurrentTool = AnnotationTool.EditText;
                    Await((Task)typeof(MainViewModel).GetMethod("BeginTextEditAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(app.Model, [app.Model.Pages[0], new NormalizedPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)])!);
                    var active = Assert.IsType<TextEditViewModel>(app.Model.ActiveTextEdit);
                    app.Model.EndTextEdit(active, "Changed line", cancel: false, apply: edit == "enter");
                    PumpUntil(() => !app.Model.IsBusy);
                }
                else if (edit == "overlay")
                {
                    app.Model.AddTextCommand.Execute(null);
                    app.Model.OnPageClicked(app.Model.Pages[0], .2, .6);
                    app.Model.CommitText(Assert.Single(app.Model.Pages[0].Items), "Overlay");
                    var picture = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Picture, 595, 842, Image());
                    picture.Bounds = new(.2, .7, .1, .1);
                    Attach(app, picture); // Exercise CommitOverlaysAsync as well as real added text.
                }
                else
                {
                    Await(app.RotateAsync()); // OpenVersionAsync, also used by Undo/Redo.
                    app.Model.UndoCommand.Execute(null);
                    PumpUntil(() => !app.Model.IsBusy);
                    app.Model.RedoCommand.Execute(null);
                    PumpUntil(() => !app.Model.IsBusy);
                }

                Await(app.Model.SaveCommand.ExecuteAsync(null));
                Assert.False(app.Model.IsDirty);
                Assert.True(app.Model.HasDocument);
                Assert.Empty(app.Dialogs.Named("ShowError"));
                Assert.NotEmpty(samples);
                Assert.DoesNotContain(samples, sample => sample.Visible);
                Assert.All(displaySamples, sample =>
                {
                    Assert.True(sample.Shows);
                    Assert.True(sample.Pages > 0);
                    Assert.Contains(Path.GetFileName(source), sample.Title);
                    Assert.Contains(Path.GetFileName(source), sample.Tab);
                });
                Assert.Contains(displaySamples, sample => sample.Reopening && !sample.HasDocument);
                Assert.False(isHomeVisible());
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.Equal(Visibility.Collapsed, home.Visibility);
            }
            finally { app.Model.PropertyChanged -= Record; }

            Await(app.Model.CloseTabAsync(keepAlive: true));
            Assert.True(isHomeVisible());
            Assert.False(app.Model.ShowsDocument);
            Assert.Empty(app.Model.Pages);
            Assert.Empty(app.Model.Bookmarks);
            Assert.Equal("YRK PDF", app.Model.WindowTitle);
            Assert.False(Await(app.OpenAsync(app.PathFor("missing.pdf"))));
            Assert.True(isHomeVisible());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Home_is_restored_when_save_reopen_fails_or_throws(bool unexpected)
    {
        WithHomeWindow((app, home, isHomeVisible, _) =>
        {
            Assert.True(Await(app.OpenAsync(app.CreatePdf())));
            Await(app.RotateAsync());
            var target = app.PathFor("reopen-failure.pdf");
            var injected = false;
            void FailReopen(object? sender, PropertyChangedEventArgs e)
            {
                // ClearEdits after the completed file has been moved, before OpenDocumentAsync.
                if (injected || app.Model.HasDocument || !File.Exists(target)) return;
                injected = true;
                if (unexpected) throw new InvalidOperationException("Injected reopen failure");
                File.Delete(target);
            }
            app.Model.PropertyChanged += FailReopen;
            try
            {
                if (unexpected)
                    Assert.Throws<InvalidOperationException>(() => Await(app.SaveToAsync(target)));
                else
                    Await(app.SaveToAsync(target));
            }
            finally { app.Model.PropertyChanged -= FailReopen; }
            Assert.True(injected);
            Assert.False(app.Model.HasDocument);
            Assert.False(app.Model.ShowsDocument);
            Assert.False(app.Model.IsReopeningDocument);
            Assert.Empty(app.Model.Pages);
            Assert.Empty(app.Model.Bookmarks);
            Assert.Equal("YRK PDF", app.Model.WindowTitle);
            Assert.False(app.Model.IsBusy);
            Assert.True(isHomeVisible());
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.Equal(Visibility.Visible, home.Visibility);
        });
    }

    private static void WithHomeWindow(Action<AppTestHarness, ScrollViewer, Func<bool>, HashSet<string>> test)
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
                window = new MainWindow(app.Model, () => throw new NotSupportedException());
                var home = (ScrollViewer)window.FindName("HomePage");
                // Read the real XAML trigger, so this also evaluates the old binding before the fix.
                var trigger = Assert.Single(home.Style.Triggers.Cast<TriggerBase>());
                var conditions = trigger is DataTrigger single
                    ? new[] { (Assert.IsType<Binding>(single.Binding).Path.Path, single.Value) }
                    : Assert.IsType<MultiDataTrigger>(trigger).Conditions.Cast<Condition>()
                        .Select(c => (Assert.IsType<Binding>(c.Binding).Path.Path, c.Value)).ToArray();
                bool IsHomeVisible() => conditions.All(c =>
                    Equals(typeof(MainViewModel).GetProperty(c.Item1)!.GetValue(app.Model), Convert.ToBoolean(c.Item2)));
                Assert.True(IsHomeVisible());
                test(app, home, IsHomeVisible, conditions.Select(c => c.Item1).ToHashSet());
            }
            finally
            {
                // Never prompt to save when an assertion interrupts a regression test.
                Await(app.Model.CloseTabAsync(keepAlive: true));
                window?.Close();
                Await(app.DisposeAsync().AsTask());
                recents.SetValue(null, oldRecents);
                application.Shutdown();
                ResetApplication();
            }
            return true;
        });
    }
}
