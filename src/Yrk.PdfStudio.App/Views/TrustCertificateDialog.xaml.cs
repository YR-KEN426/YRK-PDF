using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Input;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App.Views;

public partial class TrustCertificateDialog : Window
{
    public TrustCertificateDialog(X509Certificate2 certificate)
    {
        InitializeComponent();
        DetailsText.Text = Describe(certificate);
        Loaded += (_, _) => CancelButton.Focus();
    }

    public static string Describe(X509Certificate2 certificate) =>
        Loc.F("Name: {0}\nEmail: {1}\nOrganization: {2}\nExpires: {3:d}\nSHA-256: {4}",
            certificate.GetNameInfo(X509NameType.SimpleName, false),
            certificate.GetNameInfo(X509NameType.EmailName, false),
            string.Join(", ", certificate.SubjectName.EnumerateRelativeDistinguishedNames()
                .Where(rdn => !rdn.HasMultipleElements && rdn.GetSingleElementType().Value == "2.5.4.10")
                .Select(rdn => rdn.GetSingleElementValue())),
            certificate.NotAfter, TrustedCertificateStore.Fingerprint(certificate));

    // Even when the trust button has keyboard focus, Enter must not approve trust.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) e.Handled = true;
    }

    private void OnTrust(object sender, RoutedEventArgs e) => DialogResult = true;
}
