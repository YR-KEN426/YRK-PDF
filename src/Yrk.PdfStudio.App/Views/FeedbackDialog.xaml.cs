using System.IO;
using System.Windows;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App.Views;

/// <summary>Ways to send suggestions and problem reports; nothing is sent by the app itself.</summary>
public partial class FeedbackDialog : Window
{
    private readonly string _version;

    public FeedbackDialog(string version)
    {
        _version = version;
        InitializeComponent();
        FormButton.Visibility = Feedback.FormUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmailButton.Visibility = Feedback.Email.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        IssuesButton.Visibility = Feedback.IssuesUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmailText.Text = Feedback.Email;
        WebsiteText.Inlines.Add(Feedback.WebsiteLink(this));
    }

    private void OnForm(object sender, RoutedEventArgs e) => TryOpen(Feedback.FormUrl);

    private void OnEmail(object sender, RoutedEventArgs e) => TryOpen(Feedback.MailTo(_version));

    private void OnIssues(object sender, RoutedEventArgs e) => TryOpen(Feedback.IssuesUrl);

    private void OnCopyInfo(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(Feedback.ProgramInfo(_version));
        CopiedText.Text = Loc.T("Copied. Paste it into your message.");
    }

    private void OnLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.LogFolder);
        TryOpen(App.LogFolder);
    }

    private void TryOpen(string target) => Feedback.TryOpen(this, target);
}
