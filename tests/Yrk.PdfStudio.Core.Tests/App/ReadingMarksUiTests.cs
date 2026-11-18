using System.Reflection;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.App.Views;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class ReadingMarksUiTests
{
    [Fact]
    public void Context_menus_edit_the_clicked_page_without_navigation_or_losing_thumbnail_selection()
    {
        WithApplication(() =>
        {
            var oldStore = ReaderBookmarks.FilePath;
            var folder = Directory.CreateDirectory(PrivateTempDirectory.Path + "-marks-menu-" + Guid.NewGuid().ToString("N")).FullName;
            var app = new AppTestHarness();
            MainWindow? window = null;
            try
            {
                ReaderBookmarks.FilePath = Path.Combine(folder, "marks.json");
                var path = Path.Combine(folder, "book.pdf");
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf"), path);
                Await(app.OpenAsync(path));
                Loc.Language = "en";
                window = new MainWindow(app.Model, () => throw new NotSupportedException())
                { ShowActivated = false, Left = -16000, Top = -16000, WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                var thumbnails = (ListBox)window.FindName("ThumbnailList");
                var row = (ListBoxItem)thumbnails.ItemContainerGenerator.ContainerFromIndex(2);
                var menu = thumbnails.ContextMenu;
                menu.PlacementTarget = thumbnails;
                var item = menu.Items.OfType<MenuItem>().First();

                void OpenThumbnail(UIElement target)
                {
                    target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                        { RoutedEvent = Mouse.PreviewMouseDownEvent });
                    menu.IsOpen = true;
                }
                void ClickMark(string name)
                {
                    app.Dialogs.Answer("AskReaderBookmark", new ReaderBookmarkAnswer(name, "purple"));
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    menu.IsOpen = false;
                    Assert.Equal(0, app.Model.CurrentPageIndex);
                    Assert.Equal(2, Assert.Single(app.Model.ReadingMarks).PageIndex);
                }

                OpenThumbnail(row);
                Assert.Equal("Add reading mark…", item.Header);
                Assert.True(item.IsEnabled);
                ClickMark("Third page");
                var call = Assert.Single(app.Dialogs.Named("AskReaderBookmark"));
                Assert.Equal("Page 3", call.Arguments[0]);
                Assert.Equal(false, call.Arguments[2]);

                thumbnails.SelectedItems.Add(app.Model.Pages[0]);
                thumbnails.SelectedItems.Add(app.Model.Pages[1]);
                thumbnails.SelectedItems.Add(app.Model.Pages[2]);
                var selected = thumbnails.SelectedItems.Cast<object>().ToArray();
                OpenThumbnail(row);
                Assert.Equal("Edit reading mark…", item.Header);
                Assert.Equal(selected, thumbnails.SelectedItems.Cast<object>().ToArray());
                ClickMark("Renamed third page");
                Assert.Equal(true, app.Dialogs.Named("AskReaderBookmark").Last().Arguments[2]);

                Loc.Language = "th";
                OpenThumbnail(row);
                Assert.Equal("แก้ที่คั่น…", item.Header);
                menu.IsOpen = false;
                OpenThumbnail(thumbnails);
                Assert.Equal(Visibility.Collapsed, item.Visibility);
                menu.IsOpen = false;

                typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsBusy))!.SetValue(app.Model, true);
                OpenThumbnail(row);
                Assert.False(item.IsEnabled);
                menu.IsOpen = false;
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsBusy))!.SetValue(app.Model, false);

                var pages = (ItemsControl)window.FindName("PageItems");
                var page = Descendants<Grid>(pages).First(g => ReferenceEquals(g.DataContext, app.Model.Pages[2]) && g.Background == Brushes.White);
                page.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                    { RoutedEvent = UIElement.MouseRightButtonUpEvent });
                var pageMenu = PresentationSource.CurrentSources.Cast<PresentationSource>()
                    .Where(s => s.RootVisual is not null).SelectMany(s => Descendants<ContextMenu>(s.RootVisual))
                    .Single(m => m.IsOpen);
                var pageItem = Assert.Single(pageMenu.Items.OfType<MenuItem>());
                Assert.Equal("แก้ที่คั่น…", pageItem.Header);
                app.Dialogs.Answer("AskReaderBookmark", new ReaderBookmarkAnswer("From page", "blue"));
                pageItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                pageMenu.IsOpen = false;
                Assert.Equal(0, app.Model.CurrentPageIndex);
                Assert.Equal("From page", Assert.Single(app.Model.ReadingMarks).Name);
            }
            finally
            {
                Await(app.DisposeAsync().AsTask());
                window?.Close();
                ReaderBookmarks.FilePath = oldStore;
                Directory.Delete(folder, recursive: true);
            }
        });
    }

    [Fact]
    public void Dialog_remains_scrollable_on_small_high_scale_layouts_and_translates_both_languages()
    {
        WithApplication(() =>
        {
            foreach (var language in new[] { "en", "th" })
            foreach (var scale in new[] { 1.0, 1.5, 2.0 })
            {
                Loc.Language = language;
                var dialog = new ReaderBookmarkDialog(Loc.F("Page {0}", 3), "purple", existing: true)
                {
                    Width = 340, MaxHeight = 260, SizeToContent = SizeToContent.Manual, Height = 260,
                    ShowActivated = false, Left = -16000, Top = -16000, WindowStartupLocation = WindowStartupLocation.Manual,
                };
                try
                {
                    typeof(UiTranslator).GetMethod("TranslateNow", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [dialog]);
                    Assert.Equal(language == "th" ? "ที่คั่น" : "Reading marks", dialog.Title);
                    Assert.Equal(language == "th" ? "หน้า 3" : "Page 3", ((TextBox)dialog.FindName("NameInput")).Text);
                    Assert.Equal(40, ((TextBox)dialog.FindName("NameInput")).MaxLength);
                    var scroll = Assert.IsType<ScrollViewer>(dialog.Content);
                    scroll.LayoutTransform = new ScaleTransform(scale, scale);
                    dialog.Show();
                    dialog.Measure(new Size(340, 260));
                    dialog.Arrange(new Rect(0, 0, 340, 260));
                    dialog.UpdateLayout();
                    Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
                    Assert.True(scroll.ViewportHeight > 0);
                    var ok = (Button)dialog.FindName("OkButton");
                    Assert.True(ok.IsDefault);
                    Assert.Equal(language == "th" ? "บันทึก" : "Save", ok.Content);
                    var color = (ComboBox)dialog.FindName("ColourInput");
                    Assert.Equal(6, color.Items.Count);
                    Assert.Equal("purple", color.SelectedValue);
                    Assert.Equal(Visibility.Visible, ((Button)dialog.FindName("RemoveButton")).Visibility);
                }
                finally { dialog.Close(); }
            }
        });
    }

    [Fact]
    public void Page_and_thumbnail_ribbons_follow_marks_and_list_click_navigates_without_selecting_outline()
    {
        WithApplication(() =>
        {
            var previousStore = ReaderBookmarks.FilePath;
            var folder = Directory.CreateDirectory(PrivateTempDirectory.Path + "-marks-layout-" + Guid.NewGuid().ToString("N")).FullName;
            var app = new AppTestHarness();
            MainWindow? window = null;
            try
            {
                ReaderBookmarks.FilePath = Path.Combine(folder, "marks.json");
                var path = Path.Combine(folder, "book.pdf");
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf"), path);
                Await(app.OpenAsync(path));
                app.Dialogs.Answer("AskReaderBookmark", new ReaderBookmarkAnswer("Return here", "purple"));
                app.Model.EditReadingMark(2);
                window = new MainWindow(app.Model, () => throw new NotSupportedException())
                { ShowActivated = false, Left = -16000, Top = -16000, WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                void Layout()
                {
                    window.Measure(new Size(1100, 760));
                    window.Arrange(new Rect(0, 0, 1100, 760));
                    window.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    window.UpdateLayout();
                }
                Layout();
                var binding = Assert.Single(window.InputBindings.OfType<KeyBinding>().Where(b => b.Key == Key.D && b.Modifiers == ModifierKeys.Control));
                Assert.Same(app.Model.ReadingMarkCommand, binding.Command);
                var pages = (ItemsControl)window.FindName("PageItems");
                var ribbons = Descendants<ContentControl>(pages).Where(c => c.Style == window.Resources["ReadingMarkOverlay"]).ToArray();
                Assert.Equal(3, ribbons.Length);
                var visible = Assert.Single(ribbons.Where(c => c.Visibility == Visibility.Visible));
                Assert.Equal(2, Assert.IsType<ReadingMarkViewModel>(visible.Content).PageIndex);
                Assert.False(visible.IsHitTestVisible);
                Assert.Equal(Brushes.MediumPurple, Assert.Single(Descendants<System.Windows.Shapes.Path>(visible)).Fill);

                var thumbnails = (ListBox)window.FindName("ThumbnailList");
                var thumbnailRibbon = Assert.Single(Descendants<ContentControl>(thumbnails)
                    .Where(c => c.Style == window.Resources["ReadingMarkOverlay"] && c.Visibility == Visibility.Visible));
                Assert.Equal(2, Assert.IsType<ReadingMarkViewModel>(thumbnailRibbon.Content).PageIndex);

                var list = (ListBox)window.FindName("ReadingMarkList");
                var tab = Ancestor<TabItem>(list)!;
                tab.IsSelected = true;
                Layout();
                var row = Assert.Single(Descendants<Border>(list).Where(b => b.ContextMenu is not null));
                row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                Assert.Equal(2, app.Model.CurrentPageIndex);
                Assert.Equal(new[] { "Rename", "Change colour", "Remove" }, row.ContextMenu.Items.OfType<MenuItem>().Select(m => m.Header));
                // ContextMenu opens in a separate tree; its row must still be the edit target.
                row.ContextMenu.PlacementTarget = row;
                row.ContextMenu.IsOpen = true;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.IsType<ReadingMarkViewModel>(row.ContextMenu.DataContext);
                app.Dialogs.Answer("AskReaderBookmark", new ReaderBookmarkAnswer("Renamed", "red"));
                row.ContextMenu.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                row.ContextMenu.IsOpen = false;
                Layout();
                Assert.Equal("Renamed", Assert.Single(app.Model.ReadingMarks).Name);
                Assert.Equal(Brushes.Crimson, Assert.Single(Descendants<System.Windows.Shapes.Path>(visible)).Fill);
                app.Model.RemoveReadingMark(2);
                Layout();
                Assert.All(ribbons, r => Assert.Equal(Visibility.Collapsed, r.Visibility));
                Assert.Equal(Visibility.Collapsed, thumbnailRibbon.Visibility);
            }
            finally
            {
                Await(app.DisposeAsync().AsTask());
                window?.Close();
                ReaderBookmarks.FilePath = previousStore;
                Directory.Delete(folder, recursive: true);
            }
        });
    }

    private static void WithApplication(Action action)
    {
        Sta.Run(() =>
        {
            void Reset() => typeof(MainWindowLayoutTests).GetMethod("ForgetApplicationFromEarlierTest", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
            Reset();
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, typeof(MainWindow).Assembly);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YrkEasyPdf;component/Themes/Colors.xaml", UriKind.Relative) });
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YrkEasyPdf;component/Themes/Styles.xaml", UriKind.Relative) });
            var context = SynchronizationContext.Current;
            var recents = typeof(RecentFiles).GetField("s_items", BindingFlags.NonPublic | BindingFlags.Static)!;
            var oldRecents = recents.GetValue(null);
            recents.SetValue(null, new ObservableCollection<RecentFile>());
            var language = Loc.Language;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); }
            finally
            {
                Loc.Language = language;
                recents.SetValue(null, oldRecents);
                application.Shutdown();
                Reset();
                SynchronizationContext.SetSynchronizationContext(context);
            }
            return true;
        });
    }

    private static void Await(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static T? Ancestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var parent = LogicalTreeHelper.GetParent(child); parent is not null; parent = LogicalTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        return null;
    }
}
