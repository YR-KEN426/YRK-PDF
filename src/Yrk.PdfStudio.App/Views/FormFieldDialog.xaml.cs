using System.Windows;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf.Forms;

namespace Yrk.PdfStudio.App.Views;

/// <summary>Name (and choices, for a dropdown) of a new form field.</summary>
public partial class FormFieldDialog : Window
{
    private readonly PdfNewFieldKind _kind;

    /// <param name="existing">A field being changed: its current values, and a Delete button.</param>
    public FormFieldDialog(PdfNewFieldKind kind, string suggestedName, FormFieldAnswer? existing = null)
    {
        _kind = kind;
        InitializeComponent();
        if (existing is not null)
        {
            Title = Loc.T("Change form field");
            OkButton.Content = Loc.T("Apply");
            DeleteButton.Visibility = Visibility.Visible;
            OptionsBox.Text = string.Join(Environment.NewLine, existing.Options ?? []);
            RequiredBox.IsChecked = existing.Required;
        }

        Heading.Text = Loc.T(kind switch
        {
            PdfNewFieldKind.MultilineText => "New text box (several lines)",
            PdfNewFieldKind.CheckBox => "New check box",
            PdfNewFieldKind.Dropdown => "New dropdown list",
            PdfNewFieldKind.Signature => "New signature field",
            _ => "New text box",
        });
        NameBox.Text = suggestedName;
        if (existing is not null)
        {
            Heading.Text = Loc.T("Change form field");
        }

        OptionsPanel.Visibility = kind == PdfNewFieldKind.Dropdown ? Visibility.Visible : Visibility.Collapsed;
        RequiredBox.Visibility = kind == PdfNewFieldKind.CheckBox ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        Update();
    }

    public FormFieldAnswer? Answer { get; private set; }

    private List<string> Options() =>
        OptionsBox.Text.Split('\n').Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList();

    private void OnChanged(object sender, RoutedEventArgs e) => Update();

    private string? Problem()
    {
        if (NameBox.Text.Trim().Length == 0)
        {
            return Loc.T("Give the field a name.");
        }

        return _kind == PdfNewFieldKind.Dropdown && Options().Count == 0 ? Loc.T("Add at least one choice.") : null;
    }

    private void Update()
    {
        if (OkButton is null || ErrorText is null || OptionsBox is null)
        {
            return; // Raised while the window is being built.
        }

        var problem = Problem();
        ErrorText.Text = problem ?? string.Empty;
        OkButton.IsEnabled = problem is null;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        Answer = new FormFieldAnswer(NameBox.Text.Trim(), null, false, Delete: true);
        DialogResult = true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Problem() is not null)
        {
            return;
        }

        Answer = new FormFieldAnswer(NameBox.Text.Trim(), _kind == PdfNewFieldKind.Dropdown ? Options() : null, RequiredBox.IsChecked == true);
        DialogResult = true;
    }
}
