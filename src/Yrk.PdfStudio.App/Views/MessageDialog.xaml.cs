using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Yrk.PdfStudio.App.Views;

public sealed record MessageDialogButton(string Text, MessageBoxResult Result, char AccessKey, bool Primary = false);

/// <summary>Callers explicitly choose Enter and Esc/title-bar-close results; closing never implies consent.</summary>
public partial class MessageDialog : Window
{
    private readonly MessageBoxResult _dismissResult;
    public MessageBoxResult Result { get; private set; }

    public MessageDialog(string message, string caption, IReadOnlyList<MessageDialogButton> buttons,
        MessageBoxResult defaultResult, MessageBoxResult dismissResult, MessageBoxImage icon, Window? owner)
    {
        if (buttons.Count == 0 || buttons.Count(b => b.Result == defaultResult) != 1
            || buttons.Select(b => b.Result).Distinct().Count() != buttons.Count)
            throw new ArgumentException("Buttons must have distinct results and exactly one default.", nameof(buttons));
        InitializeComponent();
        _dismissResult = dismissResult;
        Result = dismissResult;
        Title = Loc.T(caption);
        MessageText.Text = message;
        Owner = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        IconText.Text = icon switch
        {
            MessageBoxImage.Question => "\uE9CE",
            MessageBoxImage.Warning => "\uE7BA",
            MessageBoxImage.Error => "\uEA39",
            _ => "\uE946",
        };
        foreach (var option in buttons)
        {
            var button = new Button
            {
                Content = new AccessText { Text = Loc.T(option.Text).Replace("_", "__") + $" (_{option.AccessKey})" },
                Tag = option.Result,
                IsDefault = option.Result == defaultResult,
                IsCancel = option.Result == dismissResult,
                Margin = new Thickness(8, 0, 0, 0),
            };
            button.SetResourceReference(StyleProperty, button.IsDefault ? "PrimaryButton" : "SecondaryButton");
            button.Click += (_, _) => { Result = option.Result; Close(); };
            Buttons.Children.Add(button);
            if (button.IsDefault) Loaded += (_, _) => button.Focus();
        }
        // Handle Escape even when dismissResult has no corresponding button (e.g. YesNo).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Result = _dismissResult;
            Close();
        };
    }

    public MessageBoxResult ShowResult()
    {
        ShowDialog();
        return Result;
    }
}
