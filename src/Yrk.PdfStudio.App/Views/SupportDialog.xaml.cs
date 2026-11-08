using System.Windows;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App.Views;

public partial class SupportDialog : Window
{
    public SupportDialog()
    {
        InitializeComponent();
    }

    private void OnKoFi(object sender, RoutedEventArgs e) =>
        Feedback.TryOpen(this, Feedback.KoFiUrl);

    private void OnGitHubSponsors(object sender, RoutedEventArgs e) =>
        Feedback.TryOpen(this, Feedback.GitHubSponsorsUrl);
}
