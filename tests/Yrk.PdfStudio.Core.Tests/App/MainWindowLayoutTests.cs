using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>
/// Builds the real main window in memory (never shown) and evaluates its bindings, so a XAML mistake such as a
/// TwoWay binding to a read-only property fails here instead of stopping the window from opening.
/// </summary>
[Collection("WPF Application")] // Only one System.Windows.Application can exist at a time; shared with ETaxTests.
public sealed class MainWindowLayoutTests
{
    [Fact]
    public void Main_window_lays_out_without_binding_errors_and_shows_download_progress_banner()
    {
        Sta.Run(() =>
        {
            // MainWindow.xaml uses root-relative pack URIs (/Assets/app.ico) that resolve against the resource
            // assembly, which is the test host here, so point it at the app assembly.
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, typeof(MainWindow).Assembly);
            ForgetApplicationFromEarlierTest();
            var application = new Application();
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/YrkEasyPdf;component/Themes/Styles.xaml", UriKind.Relative),
            });
            var dispatcherErrors = new List<Exception>();
            Dispatcher.CurrentDispatcher.UnhandledException += (_, e) => { dispatcherErrors.Add(e.Exception); e.Handled = true; };
            AppTestHarness? harness = null;
            var modelType = typeof(MainViewModel);
            var setState = modelType.GetMethod("SetUpdateState", BindingFlags.NonPublic | BindingFlags.Static)!;
            var none = Enum.Parse(modelType.GetNestedType("UpdateState", BindingFlags.NonPublic)!, "None");
            try
            {
                harness = new AppTestHarness();
                var window = new MainWindow(harness.Model, () => throw new NotSupportedException("Tabs are not created in this test."));

                // Bindings activate from the dispatcher queue, so run it until idle; each pass is followed by a layout.
                void Settle()
                {
                    window.Measure(new Size(1200, 800));
                    window.Arrange(new Rect(0, 0, 1200, 800));
                    window.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    window.UpdateLayout();
                    Assert.True(dispatcherErrors.Count == 0, string.Join(Environment.NewLine, dispatcherErrors));
                }

                Settle();
                // Proof that bindings really were evaluated: the hidden update banner follows the view model.
                var banner = (Border)window.FindName("UpdateBanner");
                Assert.Equal(Visibility.Collapsed, banner.Visibility);

                // The banner while a download is running.
                var progress = new UpdateDownloadProgress(30_000_000);
                progress.Report(42, DateTimeOffset.UtcNow);
                SetStatic("s_downloadProgress", progress);
                SetStatic("s_downloadingVersion", "9.9.9-test");
                SetStatic("s_downloadSize", 30_000_000L);
                setState.Invoke(null, [Enum.Parse(modelType.GetNestedType("UpdateState", BindingFlags.NonPublic)!, "Downloading")]);
                Settle();

                Assert.Equal(Visibility.Visible, banner.Visibility);
                var bar = FindVisualChild<ProgressBar>(banner);
                Assert.NotNull(bar);
                Assert.Equal(42, bar!.Value);
                Assert.Equal(Visibility.Visible, bar.Visibility);
                return true;
            }
            finally
            {
                setState.Invoke(null, [none]);
                SetStatic("s_downloadProgress", null);
                SetStatic("s_downloadingVersion", null);
                SetStatic("s_downloadSize", 0L);
                if (harness is not null) harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
                application.Shutdown();
                ForgetApplicationFromEarlierTest();
            }

            static void SetStatic(string name, object? value) =>
                typeof(MainViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);
        });
    }

    // Shutdown() without Run() leaves Application.Current set, and a second Application on another thread then throws.
    private static void ForgetApplicationFromEarlierTest()
    {
        // Also drop the cached resource manager, which remembers the first resource assembly it was asked about.
        var container = typeof(Application).Assembly.GetType("MS.Internal.AppModel.ResourceContainer")!;
        container.GetField("s_applicationResourceManagerWrapper", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        typeof(Application).GetField("_isShuttingDown", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
        foreach (var name in new[] { "_appInstance", "_appCreatedInThisAppDomain" })
            typeof(Application).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, name == "_appInstance" ? null : false);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }

        return null;
    }
}
