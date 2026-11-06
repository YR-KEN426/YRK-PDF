using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using Microsoft.Win32;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App.Views;

public partial class TrustedCertificatesDialog : Window
{
    private readonly TrustedCertificateStore _store;
    private IReadOnlyList<X509Certificate2> _certificates = [];
    public sealed record Entry(X509Certificate2 Certificate, string Description);

    public TrustedCertificatesDialog(TrustedCertificateStore store)
    {
        InitializeComponent();
        _store = store;
        Run(Refresh);
        Closed += (_, _) => { foreach (var certificate in _certificates) certificate.Dispose(); };
    }

    private void Refresh()
    {
        foreach (var certificate in _certificates) certificate.Dispose();
        _certificates = _store.LoadAll();
        CertificateList.ItemsSource = _certificates.Select(c => new Entry(c, TrustCertificateDialog.Describe(c))).ToList();
    }

    private void OnImport(object sender, RoutedEventArgs e) => Run(() =>
    {
        var picker = new OpenFileDialog { Filter = Loc.T("Certificates (*.cer;*.crt)|*.cer;*.crt"), CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        using var certificate = TrustedCertificateStore.ReadImport(picker.FileName);
        if (new TrustCertificateDialog(certificate) { Owner = this }.ShowDialog() != true) return;
        _store.Add(certificate);
        Refresh();
    });

    private void OnView(object sender, RoutedEventArgs e)
    {
        if (CertificateList.SelectedItem is Entry entry) new DialogService().ShowCertificate(entry.Certificate);
    }

    private void OnDelete(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (CertificateList.SelectedItem is not Entry entry) return;
        if (MessageBox.Show(this, Loc.T("Remove this certificate from your trusted list?"), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _store.Delete(entry.Certificate);
        Refresh();
    });

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
