using System.Windows;
using Yrk.PdfStudio.Core.Pdf.Ocr;

namespace Yrk.PdfStudio.App.Views;

public partial class OcrDialog : Window
{
    private static readonly string[] DefaultLanguages = ["tha", "eng"];

    private readonly List<LanguageChoice> _choices;

    public OcrDialog()
    {
        InitializeComponent();
        var data = new OcrLanguageData();
        _choices = OcrLanguageData.Languages
            .Select(l => new LanguageChoice(l.Code, l.Name, data.IsInstalled(l.Code) ? string.Empty : " (download)")
            {
                IsChecked = DefaultLanguages.Contains(l.Code),
            })
            .ToList();
        LanguageList.ItemsSource = _choices;
    }

    public OcrOptions? Options { get; private set; }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        var languages = _choices.Where(c => c.IsChecked).Select(c => c.Code).ToList();
        if (languages.Count == 0)
        {
            ErrorText.Text = Loc.T("Choose at least one language.");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Options = new OcrOptions(languages, AllPages.IsChecked == true ? OcrPageSelection.AllPages : OcrPageSelection.PagesWithoutText);
        DialogResult = true;
    }

    public sealed class LanguageChoice(string code, string name, string note)
    {
        public string Code { get; } = code;

        public string Name { get; } = name;

        public string Note { get; } = note;

        public bool IsChecked { get; set; }
    }
}
