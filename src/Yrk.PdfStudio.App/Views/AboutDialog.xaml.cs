using System.IO;
using System.Text;
using System.Windows;
using Yrk.PdfStudio.App.ViewModels;

namespace Yrk.PdfStudio.App.Views;

public partial class AboutDialog : Window
{
    private readonly MainViewModel.AboutModel _model;

    public AboutDialog(MainViewModel.AboutModel model)
    {
        InitializeComponent();
        _model = model;
        VersionText.Text = Loc.F("Version {0}", model.Version);
        WebsiteText.Inlines.Add(Services.Feedback.WebsiteLink(this));
        SourceText.Text = model.UpdateSource;
        AutoCheckBox.IsChecked = model.CheckAutomatically;
        AutoCheckBox.IsEnabled = model.CanUpdate;
        CheckButton.IsEnabled = model.CanUpdate;
        AutoCheckBox.Checked += (_, _) => _model.CheckAutomatically = true;
        AutoCheckBox.Unchecked += (_, _) => _model.CheckAutomatically = false;

        NoticesText.Text = Notices();
    }

    /// <summary>
    /// The program's own notice, the summary of open-source components, the program's license, and every
    /// license text in the licenses folder.
    /// </summary>
    private static string Notices()
    {
        var summary = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        if (!File.Exists(summary))
            return Loc.T("THIRD-PARTY-NOTICES.md is missing from the installation.");

        var text = new StringBuilder();
        var notice = Path.Combine(AppContext.BaseDirectory, "NOTICE.txt");
        if (File.Exists(notice))
            text.Append(File.ReadAllText(notice)).Append("\n\n");
        text.Append(File.ReadAllText(summary));
        var license = Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
        if (File.Exists(license))
            text.Append($"\n\n==== {Path.GetFileNameWithoutExtension(license)} ====\n\n").Append(File.ReadAllText(license));
        var folder = Path.Combine(AppContext.BaseDirectory, "licenses");
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetRelativePath(folder, file).Replace('\\', '/');
                text.Append($"\n\n==== licenses/{name} ====\n\n").Append(File.ReadAllText(file));
            }
        }
        return text.ToString();
    }

    private async void OnCheckNow(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        ResultText.Text = Loc.T("Checking…");
        ResultText.Text = await _model.CheckNowAsync();
        CheckButton.IsEnabled = true;
    }
}
