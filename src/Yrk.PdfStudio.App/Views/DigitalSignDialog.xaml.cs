using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using Microsoft.Win32;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.App.Views;

public partial class DigitalSignDialog : Window
{
    private readonly DigitalIdStore _store;
    private readonly bool _lockOnly;
    private readonly DigitalSignSettings _settings = DigitalSignSettings.Load();

    /// <param name="lockOnly">The handwritten signature is already on the page: sign invisibly by default.</param>
    public DigitalSignDialog(DigitalIdStore store, IReadOnlyList<SavedSignature> signatures, bool lockOnly = false)
    {
        InitializeComponent();
        _store = store;
        _lockOnly = lockOnly;

        var handwritten = new List<HandwrittenOption> { new(Loc.T("Signer's name (no handwritten signature)"), null) };
        handwritten.AddRange(signatures.Select(s => new HandwrittenOption(string.Empty, s)));
        HandwrittenChoice.ItemsSource = handwritten;
        HandwrittenChoice.SelectedItem = handwritten.FirstOrDefault(h => h.Signature?.Id == _settings.HandwrittenId) ?? handwritten[0];

        ReasonInput.Text = _settings.Reason ?? string.Empty;
        LocationInput.Text = _settings.Location ?? string.Empty;
        (_settings.Visible && !lockOnly ? VisibleOption : InvisibleOption).IsChecked = true;
        TimestampBox.IsChecked = _settings.Timestamp;
        TimestampUrlInput.Text = _settings.TimestampUrl;
        LongTermBox.IsChecked = _settings.LongTermValidation;
        LoadIds(_settings.LastId);
    }

    public DigitalSignOptions? Options { get; private set; }

    private static string Key(DigitalId id) => id.FilePath ?? id.Thumbprint ?? id.Name;

    private void LoadIds(string? select)
    {
        IReadOnlyList<DigitalId> ids;
        try
        {
            ids = _store.LoadAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            ids = [];
            ShowError(Loc.T("Couldn't read the digital IDs: ") + ex.Message);
        }

        IdList.ItemsSource = ids;
        IdList.SelectedItem = ids.FirstOrDefault(i => Key(i) == select) ?? ids.FirstOrDefault(i => !i.IsExpired);
        NoIdsText.Visibility = ids.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnIdSelectionChanged(object sender, RoutedEventArgs e)
    {
        PasswordPanel.Visibility = IdList.SelectedItem is DigitalId { IsFile: true } ? Visibility.Visible : Visibility.Collapsed;
        PasswordInput.Clear();
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnAppearanceChanged(object sender, RoutedEventArgs e)
    {
        if (HandwrittenChoice is not null)
        {
            HandwrittenChoice.IsEnabled = VisibleOption.IsChecked == true;
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => LoadIds((IdList.SelectedItem as DigitalId) is { } id ? Key(id) : null);

    private void OnCreateId(object sender, RoutedEventArgs e)
    {
        var dialog = new CreateDigitalIdDialog { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var id = _store.Create(dialog.SignerName, dialog.Email, dialog.Organization, dialog.Password);
            LoadIds(Key(id));
            PasswordInput.Password = dialog.Password;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            ShowError(Loc.T("The digital ID could not be created: ") + ex.Message);
        }
    }

    private void OnAddFile(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = Loc.T("Add a digital ID file"),
            Filter = Loc.T("Digital ID files (*.pfx;*.p12)|*.pfx;*.p12"),
            CheckFileExists = true,
        };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        var failed = false;
        while (true)
        {
            var prompt = new PasswordDialog(Path.GetFileName(picker.FileName), failed) { Owner = this };
            if (prompt.ShowDialog() != true)
            {
                return;
            }

            try
            {
                var id = _store.Import(picker.FileName, prompt.Password);
                LoadIds(Key(id));
                PasswordInput.Password = prompt.Password;
                return;
            }
            catch (CryptographicException)
            {
                failed = true; // Wrong password (or not a valid ID file): ask again.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowError(Loc.T("The file could not be added: ") + ex.Message);
                return;
            }
        }
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (IdList.SelectedItem is not DigitalId id)
        {
            ShowError(Loc.T("Choose a digital ID first."));
            return;
        }

        Uri? timestampUrl = null;
        if (TimestampBox.IsChecked == true
            && (!Uri.TryCreate(TimestampUrlInput.Text.Trim(), UriKind.Absolute, out timestampUrl) || timestampUrl.Scheme is not ("http" or "https")))
        {
            ShowError(Loc.T("Enter the web address of a timestamp server, for example ") + DigitalSignSettings.DefaultTimestampUrl);
            return;
        }

        X509Certificate2 certificate;
        try
        {
            certificate = _store.Open(id, id.IsFile ? PasswordInput.Password : null);
        }
        catch (CryptographicException)
        {
            ShowError(Loc.T(id.IsFile ? "The password is incorrect." : "The certificate could not be opened."));
            return;
        }

        string? problem = !certificate.HasPrivateKey ? Loc.T("This ID has no private key, so it cannot sign.")
            : certificate.NotAfter < DateTime.Now ? Loc.F("This ID expired on {0:d}. Use a valid ID.", certificate.NotAfter)
            : certificate.NotBefore > DateTime.Now ? Loc.T("This ID is not valid yet.")
            : null;
        if (problem is not null)
        {
            certificate.Dispose();
            ShowError(problem);
            return;
        }

        var handwritten = VisibleOption.IsChecked == true ? (HandwrittenChoice.SelectedItem as HandwrittenOption)?.Signature : null;
        Options = new DigitalSignOptions(
            certificate,
            Clean(ReasonInput.Text),
            Clean(LocationInput.Text),
            VisibleOption.IsChecked == true,
            handwritten?.Image,
            timestampUrl,
            (CertifyChoice.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag is string tag ? (PdfCertification)int.Parse(tag, System.Globalization.CultureInfo.InvariantCulture) : PdfCertification.None,
            LongTermBox.IsChecked == true);
        _settings.LongTermValidation = Options.LongTermValidation;

        _settings.LastId = Key(id);
        _settings.Reason = Options.Reason;
        _settings.Location = Options.Location;
        if (!_lockOnly)
        {
            _settings.Visible = Options.Visible; // Not when locking a hand-signed page: keep the usual choice.
        }

        _settings.HandwrittenId = handwritten?.Id;
        _settings.Timestamp = timestampUrl is not null;
        if (timestampUrl is not null)
        {
            _settings.TimestampUrl = timestampUrl.ToString();
        }

        _settings.Save();
        DialogResult = true;
    }

    /// <summary>An entry of the handwritten-signature list; the first one (no signature) has a label instead.</summary>
    public sealed record HandwrittenOption(string Label, SavedSignature? Signature);

    private static string? Clean(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
