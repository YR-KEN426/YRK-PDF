using System.IO;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// e-Tax Invoice: a form for the invoice, then a PDF/A-3 file with the ETDA XML inside, opened so it can be checked
/// and signed with a digital ID.
/// </summary>
public sealed partial class MainViewModel
{
    private IAsyncRelayCommand? _eTaxInvoiceCommand;

    public IAsyncRelayCommand ETaxInvoiceCommand => _eTaxInvoiceCommand ??= new AsyncRelayCommand(MakeETaxInvoiceAsync);

    private async Task MakeETaxInvoiceAsync()
    {
        var settings = ETaxSettings.Load();
        if (_dialogs.AskETaxInvoice(settings) is not { } invoice)
        {
            return;
        }

        settings.Seller = invoice.Seller;
        settings.LastNumber = invoice.Number;
        settings.Type = invoice.Type;
        settings.VatRate = invoice.VatRate;
        settings.PricesIncludeVat = invoice.PricesIncludeVat;
        settings.Save();

        var folder = _document is { } open ? Path.GetDirectoryName(open.FilePath) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (_dialogs.PickSavePdfFile(Sanitize(invoice.Number) + ".pdf", folder, "Save the e-Tax Invoice") is not { } output
            || !CheckNotOpenFile(output))
        {
            return;
        }

        var done = await RunBusyAsync("Making the e-Tax Invoice…", async () =>
        {
            var pdf = TaxInvoiceRenderer.Render(invoice, DateTimeOffset.Now); // WPF shapes the text: this thread.
            await File.WriteAllBytesAsync(output, pdf);
        });
        if (!done)
        {
            return;
        }

        await OpenFileAsync(output);
        StatusMessage = Loc.F("Saved {0}", Path.GetFileName(output));
        _dialogs.ShowInfo(Loc.F(
            "Saved \"{0}\" as PDF/A-3 with the e-Tax Invoice XML inside.\n\nCheck it, then:\n• e-Tax Invoice & e-Receipt: sign it with your company's digital ID (Digital signature).\n• e-Tax Invoice by Email: from the email address registered with the Revenue Department, send this one file to the buyer with a copy to csemail@etax.teda.th, with the subject written as the Revenue Department asks. The service stamps the time on it.",
            Path.GetFileName(output)));
    }
}
