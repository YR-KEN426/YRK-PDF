using System.Windows;
using System.Windows.Controls;

namespace Yrk.PdfStudio.App.Views;

/// <summary>Every keyboard shortcut on one page (Help ▸ Keyboard shortcuts).</summary>
public partial class ShortcutsDialog : Window
{
    private static readonly (string Group, (string Keys, string Action)[] Items)[] Groups =
    [
        ("File", [("Ctrl+O", "Open"), ("Ctrl+S", "Save"), ("Ctrl+Shift+S", "Save a copy"), ("Ctrl+P", "Print"), ("Ctrl+W", "Close file")]),
        ("Editing", [("Ctrl+Z", "Undo"), ("Ctrl+Y", "Redo"), ("Ctrl+C", "Copy selected text"), ("Delete", "Delete the selected item"), ("Esc", "Cancel")]),
        ("Find", [("Ctrl+F", "Find in document"), ("F3", "Next result"), ("Shift+F3", "Previous result")]),
        ("View", [("Ctrl++ / Ctrl+-", "Zoom in / out"), ("Ctrl+0", "Fit page"), ("Ctrl+1", "Actual size"), ("Ctrl+2", "Fit width"),
            ("Ctrl+Home / Ctrl+End", "First / last page"), ("H", "Hand tool"), ("Space (hold)", "Hand tool while held"), ("Ctrl+R", "Rulers")]),
        ("Menus", [("F1", "User guide"), ("Alt+F", "File"), ("Alt+V", "View"), ("Alt+T", "Tools"), ("Alt+S", "Settings"), ("Alt+H", "Help")]),
        ("Measuring", [("Shift", "Keep lines at 45° steps"), ("Enter", "Finish an area or length (or double-click)")]),
    ];

    public ShortcutsDialog()
    {
        InitializeComponent();
        for (var i = 0; i < Groups.Length; i++)
        {
            var column = i < 3 ? LeftColumn : RightColumn;
            var (group, items) = Groups[i];
            column.Children.Add(new TextBlock { Text = group, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
            foreach (var (keys, action) in items)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = action, TextWrapping = TextWrapping.Wrap });
                // Key names stay as printed on the keyboard.
                var key = new TextBlock { Text = keys, Tag = Localization.UiTranslator.KeepText, Margin = new Thickness(12, 0, 0, 0), Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush") };
                Grid.SetColumn(key, 1);
                row.Children.Add(key);
                column.Children.Add(row);
            }
        }
    }
}
