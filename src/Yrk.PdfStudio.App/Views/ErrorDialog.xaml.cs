using System.Windows;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App.Views;

public partial class ErrorDialog : Window
{
    private readonly ErrorInfo _error;
    private readonly Exception? _exception;
    internal Action<string> CopyText { get; set; } = Clipboard.SetText;

    public ErrorDialog(ErrorInfo error, Exception? ex = null, params object[] args)
    {
        InitializeComponent();
        _error = error;
        _exception = ex;
        MessageText.Text = args.Length == 0 ? Loc.T(error.Message) : Loc.F(error.Message, args);
        NextStepText.Text = Loc.T("What you can do:") + " " + Loc.T(error.NextStep);
        CodeText.Text = error.Code;
        DetailsText.Text = ex is null ? Loc.T("No technical details available.") :
            $"{ex.GetType().FullName}\n{ReportRedactor.Redact(ex.Message)}\nHRESULT: 0x{ex.HResult:X8}";
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            CopyText(ProblemReport.Build(AppUpdater.AssemblyVersion, _error, _exception, App.LogFolder, DateTime.Now));
            CopyFeedback.Text = Loc.T("Copied");
        }
        catch (Exception)
        {
            CopyFeedback.Text = Loc.T("Could not copy. Try again.");
        }
    }
}
