using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;

namespace Yrk.PdfStudio.App.Services;

/// <summary>
/// Where people send suggestions and problem reports. The app itself sends nothing: these open the
/// browser or the email program, and the person decides what to send. An empty link hides its button.
/// </summary>
public static class Feedback
{
    /// <summary>A Google Form for suggestions and problems (no account needed).</summary>
    public const string FormUrl = "https://docs.google.com/forms/d/e/1FAIpQLSd-H6KQaZ6cZFB6L73aECFCWo84fS7jO5z4AyMol7P8mzGuJQ/viewform";

    /// <summary>The address for feedback by email.</summary>
    public const string Email = "yrken.official@gmail.com";

    /// <summary>
    /// GitHub Issues of the source repository (needs a GitHub account). Not used yet; to show it in the menu,
    /// set it to https://github.com/YR-KEN426/YRK-PDF/issues/new/choose
    /// </summary>
    public const string IssuesUrl = "";

    /// <summary>What helps to answer a report: the version and Windows, nothing about the user or their files.</summary>
    public static string ProgramInfo(string version) =>
        $"YRK PDF {version}\n{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})\n" +
        $"Language: {Loc.Language}";

    /// <summary>The download page: newest version, features and answers to common questions.</summary>
    public const string WebsiteUrl = "https://yr-ken426.github.io/yrk-easy-pdf-release/";

    /// <summary>
    /// The website's "support the developer" answer (GitHub Sponsors). An empty link hides the menu item.
    /// </summary>
    public const string SupportUrl = WebsiteUrl + "#support";

    public static void Open(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    /// <summary>Opens a link or folder; says so when Windows can't.</summary>
    public static void TryOpen(Window owner, string target)
    {
        try
        {
            Open(target);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(owner, Loc.F("Couldn't open it.\n\n{0}", ex.Message), DialogService.Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>A link to the website (shown without "https://") for a TextBlock of <paramref name="owner"/>.</summary>
    public static Hyperlink WebsiteLink(Window owner)
    {
        var link = new Hyperlink(new Run(WebsiteUrl["https://".Length..].TrimEnd('/'))) { NavigateUri = new Uri(WebsiteUrl) };
        link.RequestNavigate += (_, e) =>
        {
            TryOpen(owner, WebsiteUrl);
            e.Handled = true;
        };
        return link;
    }

    /// <summary>A new email with the subject and the program information filled in.</summary>
    public static string MailTo(string version) =>
        $"mailto:{Email}?subject={Uri.EscapeDataString($"YRK PDF {version}")}" +
        $"&body={Uri.EscapeDataString(Loc.T("Please describe the problem or suggestion here:") + "\n\n\n\n---\n" + ProgramInfo(version))}";
}
