using System.Windows;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App.Views;

public partial class ReaderBookmarkDialog : Window
{
    public ReaderBookmarkAnswer? Answer { get; private set; }

    public ReaderBookmarkDialog(string name, string color, bool existing, bool focusColour = false)
    {
        InitializeComponent();
        MaxHeight = Math.Max(100, SystemParameters.WorkArea.Height - 24);
        MaxWidth = Math.Max(100, SystemParameters.WorkArea.Width - 24);
        NameInput.Text = name;
        ColourInput.SelectedValue = color;
        RemoveButton.Visibility = existing ? Visibility.Visible : Visibility.Collapsed;
        OkButton.Content = existing ? Loc.T("Save") : Loc.T("Add");
        Loaded += (_, _) =>
        {
            if (focusColour) ColourInput.Focus();
            else { NameInput.Focus(); NameInput.SelectAll(); }
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Answer = new(NameInput.Text, (string)ColourInput.SelectedValue);
        DialogResult = true;
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        Answer = new(NameInput.Text, (string)ColourInput.SelectedValue, Remove: true);
        DialogResult = true;
    }
}
