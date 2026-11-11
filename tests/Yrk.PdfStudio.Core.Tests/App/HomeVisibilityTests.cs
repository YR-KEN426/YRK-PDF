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
            void Record(object? sender, PropertyChangedEventArgs e)
            {
                if (string.IsNullOrEmpty(e.PropertyName) || bindingProperties.Contains(e.PropertyName))
                    samples.Add((e.PropertyName, isHomeVisible()));
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
                Assert.False(isHomeVisible());
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.Equal(Visibility.Collapsed, home.Visibility);
            }
            finally { app.Model.PropertyChanged -= Record; }

            Await(app.Model.CloseTabAsync(keepAlive: true));
            Assert.True(isHomeVisible());
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
