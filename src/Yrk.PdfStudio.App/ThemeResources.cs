using System.Windows;
using System.Windows.Shapes;

namespace Yrk.PdfStudio.App;

/// <summary>Resource references for generated visuals and view-model selected UI paint.</summary>
public static class ThemeResources
{
    public static T Apply<T>(T element, params (DependencyProperty Property, object Key)[] resources) where T : DependencyObject
    {
        foreach (var (property, key) in resources)
        {
            if (element is FrameworkElement visual) visual.SetResourceReference(property, key);
            else if (element is FrameworkContentElement content) content.SetResourceReference(property, key);
            else throw new ArgumentException("A resource reference needs a framework element.", nameof(element));
        }
        return element;
    }

    public static readonly DependencyProperty FillKeyProperty = DependencyProperty.RegisterAttached(
        "FillKey", typeof(string), typeof(ThemeResources), new PropertyMetadata(null, OnFillKeyChanged));
    public static string? GetFillKey(DependencyObject element) => (string?)element.GetValue(FillKeyProperty);
    public static void SetFillKey(DependencyObject element, string? value) => element.SetValue(FillKeyProperty, value);
    private static void OnFillKeyChanged(DependencyObject element, DependencyPropertyChangedEventArgs args) =>
        Update((FrameworkElement)element, Shape.FillProperty, args.NewValue);

    public static readonly DependencyProperty StrokeKeyProperty = DependencyProperty.RegisterAttached(
        "StrokeKey", typeof(string), typeof(ThemeResources), new PropertyMetadata(null, OnStrokeKeyChanged));
    public static string? GetStrokeKey(DependencyObject element) => (string?)element.GetValue(StrokeKeyProperty);
    public static void SetStrokeKey(DependencyObject element, string? value) => element.SetValue(StrokeKeyProperty, value);
    private static void OnStrokeKeyChanged(DependencyObject element, DependencyPropertyChangedEventArgs args) =>
        Update((FrameworkElement)element, Shape.StrokeProperty, args.NewValue);

    public static readonly DependencyProperty ForegroundKeyProperty = DependencyProperty.RegisterAttached(
        "ForegroundKey", typeof(string), typeof(ThemeResources), new PropertyMetadata(null, OnForegroundKeyChanged));
    public static string? GetForegroundKey(DependencyObject element) => (string?)element.GetValue(ForegroundKeyProperty);
    public static void SetForegroundKey(DependencyObject element, string? value) => element.SetValue(ForegroundKeyProperty, value);
    private static void OnForegroundKeyChanged(DependencyObject element, DependencyPropertyChangedEventArgs args) =>
        Update((FrameworkElement)element, System.Windows.Controls.TextBlock.ForegroundProperty, args.NewValue);

    private static void Update(FrameworkElement element, DependencyProperty property, object? key)
    {
        // Clearing the local resource expression reveals the style binding to document paint again.
        if (key is null) element.ClearValue(property);
        else element.SetResourceReference(property, key);
    }
}
