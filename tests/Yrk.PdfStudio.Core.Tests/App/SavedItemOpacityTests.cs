using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed partial class SavedItemEditingTests
{
    [Theory]
    [InlineData("thumb")]
    [InlineData("track")]
    public void Opacity_slider_ValueChanged_previews_then_commits_once_on_release(string gesture)
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
            Popup? popup = null;
            try
            {
                var source = Await(PlacedSource(app));
                var sourceBytes = File.ReadAllBytes(source);
                var baseline = Await(Render(app));
                Await(Select(app));
                var frame = Frame(app);
                var document = Document(app);
                var reference = frame.SavedItem!.Reference;
                window = new MainWindow(app.Model, () => throw new NotSupportedException())
                    { Width = 1000, Height = 800, Left = -10000, Top = -10000, ShowActivated = false };
                window.Show();
                ((RadioButton)window.FindName("EditTab")).IsChecked = true;
                // Changing tabs may clear selection.
                Await(Select(app)); frame = Frame(app); reference = frame.SavedItem!.Reference;
                var slider = OpenSlider();
                Assert.True(slider.IsMoveToPointEnabled);

                // Track clicks change Value before Thumb.DragStarted. Use the production handler
                // without consulting or moving the user's real pointer.
                Handler("OnItemOpacityMouseDown", slider,
                    new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left));
                slider.Value = .9;
                if (gesture == "thumb") slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                foreach (var value in new[] { .8, .6, .4, .3 }) slider.Value = value;
                Await(app.Model.ItemOpacityPreviewTask);
                Assert.Equal(.3, frame.Opacity);
                Assert.Same(document, Document(app));
                Assert.Equal(reference, Await(Items(app))[0].Reference);
                Assert.Equal(baseline.Pixels, Await(Render(app)).Pixels);
                Assert.Equal(sourceBytes, File.ReadAllBytes(source));
                Assert.False(app.Model.IsDirty);
                Assert.False(app.Model.UndoCommand.CanExecute(null));
                var preview = Assert.IsAssignableFrom<BitmapSource>(app.Model.Pages[0].OpacityPreview);
                var pixels = new byte[preview.PixelWidth * preview.PixelHeight * 4];
                preview.CopyPixels(pixels, preview.PixelWidth * 4, 0);
                // The red item's centre fades against white; the other red item stays opaque.
                Assert.InRange(pixels[((int)(preview.PixelHeight * .17) * preview.PixelWidth + (int)(preview.PixelWidth * .2)) * 4], 175, 182);
                Assert.Equal(0, pixels[((int)(preview.PixelHeight * .8) * preview.PixelWidth + (int)(preview.PixelWidth * .8)) * 4]);

                if (gesture == "thumb") slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Handler("OnItemOpacityMouseUp", slider,
                    new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left));
                Await(EditFinished(app));
                Assert.Null(app.Model.Pages[0].OpacityPreview);
                Assert.InRange(Frame(app).Opacity, .299, .303);
                Assert.NotEqual(reference, Frame(app).SavedItem!.Reference);
                Assert.Equal(2, Versions().Count); // Before + one committed change, irrespective of ValueChanged count.
                Assert.Equal(sourceBytes, File.ReadAllBytes(source));

                // Late events from the detached popup must not reopen stale-reference dialogs.
                slider.Value = .2;
                Await(EditFinished(app));
                Assert.Equal(2, Versions().Count);
                slider = OpenSlider();
                slider.Value = .7; // Keyboard/automation changes without a mouse gesture commit immediately.
                Await(EditFinished(app));
                Assert.InRange(Frame(app).Opacity, .699, .703);
                Assert.Equal(3, Versions().Count);
                Assert.Empty(app.Dialogs.Named("ShowError"));
                Assert.Empty(app.Dialogs.Named("ShowInfo"));
                app.Model.UndoCommand.Execute(null); Await(Idle(app));
                Assert.InRange(Await(Items(app))[0].Opacity, .299, .303);
                app.Model.UndoCommand.Execute(null); Await(Idle(app));
                Assert.Equal(baseline.Pixels, Await(Render(app)).Pixels);
                Assert.False(app.Model.UndoCommand.CanExecute(null));
                return true;

                IList Versions() => (IList)typeof(MainViewModel).GetField("_versions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(app.Model)!;
                Slider OpenSlider()
                {
                    if (popup is not null) popup.IsOpen = false;
                    Settle(window);
                    var overlay = Descendants<Grid>(window).Single(g => ReferenceEquals(g.DataContext, Frame(app))
                        && g.Children.OfType<System.Windows.Shapes.Rectangle>().Any(r => r.StrokeDashArray.Count > 0));
                    Descendants<ToggleButton>(overlay).Single().IsChecked = true;
                    popup = Descendants<Popup>(overlay).Single();
                    Settle(window);
                    var result = Descendants<Slider>(popup.Child).Single();
                    Assert.True(result.IsLoaded);
                    return result;
                }
                void Handler(string name, object sender, object args) => typeof(MainWindow)
                    .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [sender, args]);
            }
            finally
            {
                if (popup is not null) popup.IsOpen = false;
                if (window is not null)
                {
                    typeof(MainWindow).GetField("_closeConfirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
                    window.Close();
                }
                Await(app.DisposeAsync().AsTask());
                recents.SetValue(null, oldRecents);
                application.Shutdown(); ResetApplication();
            }
        });
    }

    [Fact]
    public async Task Canceling_opacity_preview_restores_value_pixels_and_keeps_document_unchanged()
    {
        await using var app = new AppTestHarness(); await PlacedSource(app); await Select(app);
        var frame = Frame(app);
        var before = await Render(app);
        app.Model.BeginItemOpacityChange(frame);
        app.Model.ChangeItemOpacity(frame, .2);
        await app.Model.ItemOpacityPreviewTask;
        Assert.NotNull(app.Model.Pages[0].OpacityPreview);
        app.Model.EndItemOpacityChange(frame, cancel: true);
        await EditFinished(app);
        Assert.Equal(1, frame.Opacity);
        Assert.Null(app.Model.Pages[0].OpacityPreview);
        Assert.Equal(before.Pixels, (await Render(app)).Pixels);
        Assert.False(app.Model.IsDirty);
        Assert.Empty(app.Dialogs.Named("ShowError"));
        Assert.Empty(app.Dialogs.Named("ShowInfo"));
    }

    [Fact]
    public async Task Rejected_opacity_gesture_shows_one_message_and_blocks_reentrant_slider_changes()
    {
        await using var app = new AppTestHarness(); await PlacedSource(app); await Select(app);
        var frame = Frame(app);
        var baseline = await Render(app);
        await Document(app).SaveToBytesAsync(); // Simulate another operation expiring the selected reference.
        var dialogs = (SignatureSecurityTests.TestProxy)(object)app.Dialogs.Service;
        var original = dialogs.Handle;
        var depth = 0;
        dialogs.Handle = (method, args) =>
        {
            if (method.Name is "ShowInfo" or "ShowError")
            {
                Assert.Equal(0, depth++);
                try
                {
                    app.Model.ChangeItemOpacity(frame, .8);
                    app.Model.BeginItemOpacityChange(frame);
                    app.Model.ChangeItemOpacity(frame, .7);
                    app.Model.EndItemOpacityChange(frame);
                }
                finally { --depth; }
            }
            return original(method, args);
        };
        app.Model.BeginItemOpacityChange(frame);
        foreach (var alpha in new[] { .9, .5, .2 }) app.Model.ChangeItemOpacity(frame, alpha);
        app.Model.EndItemOpacityChange(frame);
        await EditFinished(app);
        Assert.Single(app.Dialogs.Named("ShowInfo"));
        Assert.Empty(app.Dialogs.Named("ShowError"));
        Assert.False(app.Model.IsBusy);
        Assert.False(app.Model.IsDirty);
        Assert.Null(app.Model.Pages[0].OpacityPreview);
        Assert.Equal(baseline.Pixels, (await Render(app)).Pixels);
        await Select(app);
        app.Model.ChangeItemOpacity(Frame(app), .5); await EditFinished(app);
        Assert.InRange(Frame(app).Opacity, .499, .503);
        Assert.Single(app.Dialogs.Named("ShowInfo"));
    }
}
