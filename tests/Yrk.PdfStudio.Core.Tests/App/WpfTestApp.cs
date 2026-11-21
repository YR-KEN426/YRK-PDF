using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Yrk.PdfStudio.App.Localization;

namespace Yrk.PdfStudio.Testing;

/// <summary>Shared by the window tests and renderer. Never runs App startup, updater or settings services.</summary>
internal sealed class WpfTestApp : IDisposable
{
    private readonly Application _application;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
    private readonly string _language = Loc.Language;
    private readonly ErrorListener _listener = new();
    private readonly List<(TraceSource Source, SourceLevels Level)> _sources = [];
    public List<string> Errors { get; } = [];

    public WpfTestApp(string language, string theme = "light")
    {
        var palettePath = WindowCatalog.PalettePath(theme);
        ResetApplication();
        typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, typeof(Yrk.PdfStudio.App.MainWindow).Assembly);
        _application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        _listener.Messages = Errors;
        PresentationTraceSources.Refresh();
        foreach (var source in new[] { PresentationTraceSources.DataBindingSource, PresentationTraceSources.ResourceDictionarySource,
                     PresentationTraceSources.MarkupSource })
        {
            _sources.Add((source, source.Switch.Level));
            source.Switch.Level = SourceLevels.Warning;
            source.Listeners.Add(_listener);
        }
        _application.DispatcherUnhandledException += OnUnhandled;
        // Embed the real App.xaml in both hosts: dictionary additions need no second hardcoded resource list.
        using var sourceXaml = typeof(WpfTestApp).Assembly.GetManifestResourceStream("WindowRenderer.AppResources.xaml")!;
        var applicationXaml = XDocument.Load(sourceXaml).Root!;
        var dictionary = applicationXaml.Descendants().First(e => e.Name.LocalName == "ResourceDictionary");
        foreach (var attribute in applicationXaml.Attributes().Where(a => a.IsNamespaceDeclaration))
            if (dictionary.Attribute(attribute.Name) is null) dictionary.Add(new XAttribute(attribute));
        // Publish the root before loading its merged dictionaries, like Application.InitializeComponent.
        // Loading the whole dictionary off-tree leaves StaticResource references in Styles templates
        // unresolved: Colors must already be visible in Application.Current.Resources during parsing.
        var merged = dictionary.Elements().SingleOrDefault(e => e.Name.LocalName == "ResourceDictionary.MergedDictionaries");
        var entries = merged?.Elements().ToArray() ?? [];
        merged?.Remove();
        var parser = new ParserContext { BaseUri = new Uri("pack://application:,,,/YrkEasyPdf;component/App.xaml") };
        using var xml = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(dictionary.ToString()));
        _application.Resources = (ResourceDictionary)XamlReader.Load(xml, parser);
        foreach (var entry in entries)
        {
            // Select the real palette before parsing Styles, matching App's resource order.
            if ((string?)entry.Attribute("Source") == WindowCatalog.PalettePath("light"))
                entry.SetAttributeValue("Source", palettePath);
            using var entryXml = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(entry.ToString()));
            _application.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(entryXml, parser));
        }
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        Loc.Language = language;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Errors.Add(e.Exception.ToString());
        e.Handled = true;
    }

    public static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    public static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(30) };
            timeout.Tick += (_, _) => frame.Continue = false;
            timeout.Start();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            if (!task.IsCompleted) throw new TimeoutException("WPF operation did not complete in 30 seconds.");
        }
        task.GetAwaiter().GetResult();
    }

    public static void Layout(Window window)
    {
        var width = double.IsFinite(window.Width) ? window.Width : 1000;
        var height = double.IsFinite(window.Height) ? window.Height : 800;
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();
        Pump();
        Translate(window);
        window.UpdateLayout();
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    /// <summary>Visit tabs as well as the initially visible tree, then materialize each popup template.</summary>
    public static int ExercisePopups(Window window, Action<Control>? inspectTemplate = null)
    {
        var count = 0;
        var seen = new HashSet<DependencyObject>();
        void OpenVisible()
        {
            Layout(window);
            foreach (var control in Descendants(window).Where(c => c is ComboBox or DatePicker).ToArray())
            {
                if (!seen.Add(control)) continue;
                if (control is ComboBox combo)
                {
                    combo.ApplyTemplate();
                    inspectTemplate?.Invoke(combo);
                    combo.IsDropDownOpen = true;
                    Pump();
                    if (combo.Template.FindName("PART_Popup", combo) is Popup { Child: { } child })
                    {
                        child.Measure(new Size(1000, 800)); child.Arrange(new Rect(child.DesiredSize));
                        child.UpdateLayout(); Translate(child);
                    }
                    combo.IsDropDownOpen = false;
                }
                else if (control is DatePicker date)
                {
                    date.ApplyTemplate();
                    inspectTemplate?.Invoke(date);
                    date.IsDropDownOpen = true; Pump();
                    if (date.Template.FindName("PART_Popup", date) is Popup { Child: { } calendar })
                    {
                        calendar.Measure(new Size(1000, 800)); calendar.Arrange(new Rect(calendar.DesiredSize));
                        calendar.UpdateLayout(); Translate(calendar);
                    }
                    date.IsDropDownOpen = false;
                }
                count++;
                Pump();
            }
        }
        OpenVisible();
        foreach (var tabs in Descendants(window).OfType<TabControl>().ToArray())
        {
            var selected = tabs.SelectedIndex;
            for (var i = 0; i < tabs.Items.Count; i++) { tabs.SelectedIndex = i; OpenVisible(); }
            tabs.SelectedIndex = selected;
        }
        Layout(window);
        return count;
    }

    public void Dispose()
    {
        try
        {
            foreach (Window window in _application.Windows.Cast<Window>().ToArray()) window.Close();
            Pump();
            Loc.Language = _language;
            CultureInfo.CurrentUICulture = _culture;
            _application.Shutdown();
        }
        finally
        {
            _application.DispatcherUnhandledException -= OnUnhandled;
            foreach (var (source, level) in _sources) { source.Listeners.Remove(_listener); source.Switch.Level = level; }
            _listener.Dispose();
            ResetApplication();
            SynchronizationContext.SetSynchronizationContext(_context);
        }
    }

    private static void ResetApplication()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var container = typeof(Application).Assembly.GetType("MS.Internal.AppModel.ResourceContainer")!;
        container.GetField("s_applicationResourceManagerWrapper", flags)!.SetValue(null, null);
        typeof(Application).GetField("_isShuttingDown", flags)!.SetValue(null, false);
        typeof(Application).GetField("_appInstance", flags)!.SetValue(null, null);
        typeof(Application).GetField("_appCreatedInThisAppDomain", flags)!.SetValue(null, false);
    }

    private static void Translate(DependencyObject root) => typeof(UiTranslator)
        .GetMethod("TranslateNow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [root]);

    private sealed class ErrorListener : TraceListener
    {
        // Trace sources are process-wide: WPF tests outside this collection run in parallel on their own
        // threads, so keep only what this app's thread reports.
        private readonly Thread _owner = Thread.CurrentThread;
        public List<string> Messages { get; set; } = [];
        public override void Write(string? message)
        {
            if (Thread.CurrentThread == _owner && !string.IsNullOrWhiteSpace(message)) Messages.Add(message);
        }
        public override void WriteLine(string? message) => Write(message);
    }
}
