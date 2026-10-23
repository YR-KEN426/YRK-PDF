using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Yrk.PdfStudio.App.Localization;

/// <summary>
/// Translates the English texts written in XAML as elements load (windows, templates, menus), and again
/// when the language changes. Bound values are left alone: view models translate their own texts. The
/// English original is remembered per element, so switching back and forth works, and a text that code
/// changed in the meantime is taken as the new original.
/// </summary>
public static class UiTranslator
{
    private static readonly DependencyProperty[] ElementProperties =
    [
        TextBlock.TextProperty,
        ContentControl.ContentProperty,
        HeaderedContentControl.HeaderProperty,
        HeaderedItemsControl.HeaderProperty,
        FrameworkElement.ToolTipProperty,
        Window.TitleProperty,
        AutomationProperties.NameProperty,
    ];

    private static readonly DependencyProperty OriginalsProperty = DependencyProperty.RegisterAttached(
        "Originals", typeof(Dictionary<DependencyProperty, (string English, string Shown)>), typeof(UiTranslator));

    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(UiTranslator));

    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);
    private static readonly HashSet<Window> Scheduled = [];
    private static bool _everTranslated;

    /// <summary>
    /// WPF sends Loaded only to elements with their own handler, so a class handler would miss most of them.
    /// Instead every window is attached on its first size change, and its whole tree is translated after
    /// layout changes (new templates, panels shown later) at most every 250 ms. Menus and tooltips live in
    /// their own popups and are translated when they open. Nothing runs while the UI stays English.
    /// </summary>
    public static void Start()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((sender, _) => Attach((Window)sender)));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((sender, _) => TranslateNow((DependencyObject)sender)));
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((sender, _) => TranslateNow((DependencyObject)sender)));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((sender, _) => TranslateNow((DependencyObject)sender)));
        Loc.LanguageChanged += (_, _) =>
        {
            foreach (Window window in Application.Current.Windows)
            {
                TranslateNow(window);
            }
        };
    }

    private static bool IsNeeded => Loc.IsThai || _everTranslated;

    private static void Attach(Window window)
    {
        if ((bool)window.GetValue(AttachedProperty))
        {
            return;
        }

        window.SetValue(AttachedProperty, true);
        var last = DateTime.MinValue;
        window.LayoutUpdated += (_, _) =>
        {
            if (!IsNeeded || !Scheduled.Add(window))
            {
                return;
            }

            var wait = last + MinInterval - DateTime.UtcNow;
            var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = wait > TimeSpan.Zero ? wait : TimeSpan.Zero };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Scheduled.Remove(window);
                last = DateTime.UtcNow;
                TranslateNow(window);
            };
            timer.Start();
        };
        TranslateNow(window);
    }

    private static void TranslateNow(DependencyObject root)
    {
        if (IsNeeded)
        {
            _everTranslated = true;
            TranslateTree(root, []);
        }
    }

    private static void TranslateTree(DependencyObject node, HashSet<DependencyObject> seen)
    {
        if (!seen.Add(node))
        {
            return;
        }

        Translate(node);
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToList()) // Translating a Run changes its parent's list.
        {
            TranslateTree(child, seen);
        }

        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                TranslateTree(VisualTreeHelper.GetChild(node, i), seen);
            }
        }
    }

    /// <summary>Put in an element's Tag to keep its text as written (for example language names).</summary>
    public const string KeepText = "keep-text";

    private static void Translate(DependencyObject node)
    {
        if (node is FrameworkElement element)
        {
            if (element.Tag is KeepText)
            {
                return;
            }

            foreach (var property in ElementProperties)
            {
                if (property.OwnerType.IsInstanceOfType(element) || property == FrameworkElement.ToolTipProperty || property == AutomationProperties.NameProperty)
                {
                    TranslateProperty(element, property);
                }
            }

            if (element is TextBlock { Inlines.Count: > 0 } block && DependencyPropertyHelper.GetValueSource(block, TextBlock.TextProperty).BaseValueSource == BaseValueSource.Default)
            {
                foreach (var run in block.Inlines.OfType<Run>().ToList())
                {
                    TranslateProperty(run, Run.TextProperty);
                }
            }
        }
        else if (node is Run run)
        {
            TranslateProperty(run, Run.TextProperty);
        }
    }

    private static void TranslateProperty(DependencyObject target, DependencyProperty property)
    {
        if (BindingOperations.IsDataBound(target, property) || target.GetValue(property) is not string { Length: > 0 } value)
        {
            return;
        }

        var source = DependencyPropertyHelper.GetValueSource(target, property);
        if (source.IsExpression || source.BaseValueSource is not (BaseValueSource.Local or BaseValueSource.ParentTemplate or BaseValueSource.Style or BaseValueSource.DefaultStyle))
        {
            return;
        }

        var originals = (Dictionary<DependencyProperty, (string English, string Shown)>?)target.GetValue(OriginalsProperty);
        var english = originals is not null && originals.TryGetValue(property, out var known) && known.Shown == value ? known.English : value;
        var text = Loc.T(english);
        if (text == english && originals is null && text == value)
        {
            return; // Nothing to translate and nothing to restore.
        }

        if (originals is null)
        {
            originals = [];
            target.SetValue(OriginalsProperty, originals);
        }

        originals[property] = (english, text);
        if (text != value)
        {
            target.SetCurrentValue(property, text);
        }
    }
}
