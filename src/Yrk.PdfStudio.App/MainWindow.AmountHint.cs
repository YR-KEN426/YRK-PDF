using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.App;

/// <summary>
/// A small offer under a text box on the page while it holds just an amount: one click writes it out in words, for
/// people who would never look in the right-click menu. Added text that is already placed has a right-click menu too.
/// </summary>
public partial class MainWindow
{
    private readonly Popup _amountHint = new()
    {
        AllowsTransparency = true,
        Placement = PlacementMode.Bottom,
        VerticalOffset = 3,
        StaysOpen = true,
    };

    private TextBox? _amountBox;

    private void InitializeAmountHint()
    {
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, e) => ShowAmountHint(e.OriginalSource as TextBox)));
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) => ShowAmountHint(e.NewFocus as TextBox)));
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => HideAmountHint()));
        PageScroller.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0 || e.HorizontalChange != 0)
            {
                HideAmountHint(); // It would stay behind.
            }
        };
        Deactivated += (_, _) => HideAmountHint();
    }

    private void ShowAmountHint(TextBox? box)
    {
        if (box is null || !box.IsKeyboardFocused || !IsTypingBox(box) || !AmountInWords.LooksLikeAmount(box.Text))
        {
            HideAmountHint();
            return;
        }

        _amountBox = box;
        _amountHint.PlacementTarget = box;
        _amountHint.Child ??= BuildAmountHint();
        _amountHint.IsOpen = true;
    }

    private void HideAmountHint()
    {
        _amountHint.IsOpen = false;
        _amountHint.Child = null; // Built again in the language shown.
        _amountBox = null;
    }

    /// <summary>The boxes that have the typing menu: form fields, added text and a line being edited.</summary>
    private static bool IsTypingBox(TextBox box) =>
        box.ContextMenu?.Items.OfType<MenuItem>().Any(i => i.Tag is "th") == true;

    private Border BuildAmountHint()
    {
        Button Choice(string text, bool english)
        {
            var button = new Button
            {
                Content = text,
                Tag = english,
                Style = (Style)FindResource("SecondaryButton"),
                Focusable = false, // The box keeps the focus, which keeps its editing going.
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(8, 2, 8, 2),
                MinHeight = 0,
                MinWidth = 0,
            };
            AutomationProperties.SetName(button, Loc.T("Amount in words") + " " + text);
            button.Click += OnAmountHintClick;
            return button;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = Loc.T("Amount in words"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Tag = UiTranslator.KeepText });
        row.Children.Add(Choice(Loc.T("Thai"), english: false));
        row.Children.Add(Choice(Loc.T("English"), english: true));
        return new Border
        {
            Child = row,
            Padding = new Thickness(8, 4, 6, 4),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            Background = (Brush)FindResource("SurfaceBrush"),
        };
    }

    private void OnAmountHintClick(object sender, RoutedEventArgs e)
    {
        if (_amountBox is { } box && AmountIn(box) is { } found && sender is FrameworkElement { Tag: bool english })
        {
            WriteAmountInWords(box, found, english);
        }
    }

    /// <summary>Right-click on added text that is placed, not being edited: the same words, without opening it first.</summary>
    private void OnItemRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not { IsText: true, IsEditing: false } item
            || AmountInWords.Find(item.Text, item.Text.Length) is not { } found)
        {
            return;
        }

        MenuItem Choice(string header, bool english)
        {
            var choice = new MenuItem { Header = header };
            choice.Click += (_, _) =>
            {
                var words = english ? AmountInWords.English(found.Amount) : AmountInWords.Thai(found.Amount);
                _viewModel.ReplaceText(item, string.Concat(item.Text.AsSpan(0, found.Start), words, item.Text.AsSpan(found.Start + found.Length)));
            };
            return choice;
        }

        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = (UIElement)sender };
        menu.Items.Add(Choice("Amount in Thai words", english: false));
        menu.Items.Add(Choice("Amount in English words", english: true));
        menu.IsOpen = true;
        e.Handled = true;
    }
}
