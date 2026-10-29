using System.Windows;
using System.Windows.Controls;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.App;

/// <summary>
/// The right-click menu of the boxes text is typed into on a page (form fields, added text, a line being edited):
/// besides cut, copy and paste, it writes an amount out in words, as receipts and quotations need
/// ("10000" becomes "หนึ่งหมื่นบาทถ้วน").
/// </summary>
public partial class MainWindow
{
    private static TextBox? TypingBox(object sender) => sender switch
    {
        System.Windows.Controls.ContextMenu { PlacementTarget: TextBox box } => box,
        MenuItem { Parent: System.Windows.Controls.ContextMenu { PlacementTarget: TextBox box } } => box,
        _ => null,
    };

    /// <summary>A menu opening takes the focus from the box, which must not end the editing.</summary>
    private static bool IsMenu(IInputElement? element) => element is MenuItem or System.Windows.Controls.ContextMenu;

    private static (int Start, int Length, decimal Amount)? AmountIn(TextBox box) =>
        AmountInWords.Find(box.Text, box.CaretIndex, box.SelectionStart, box.SelectionLength);

    private void OnTypingMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.ContextMenu menu && TypingBox(menu) is { } box)
        {
            var found = AmountIn(box) is not null;
            foreach (var item in menu.Items.OfType<MenuItem>().Where(i => i.Tag is string))
            {
                item.IsEnabled = found;
            }
        }
    }

    private void OnAmountInWords(object sender, RoutedEventArgs e)
    {
        if (TypingBox(sender) is not { } box || AmountIn(box) is not { } found)
        {
            return;
        }

        var words = (sender as MenuItem)?.Tag as string == "en" ? AmountInWords.English(found.Amount) : AmountInWords.Thai(found.Amount);

        // Through the selection, so Ctrl+Z in the box brings the number back.
        box.Focus();
        box.Select(found.Start, found.Length);
        box.SelectedText = words;
        box.Select(found.Start + words.Length, 0);
    }
}
