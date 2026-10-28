using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>Digital signatures: signing with a certificate and the Signatures panel.</summary>
public sealed partial class MainViewModel
{
    private const double DefaultDigitalSignatureWidth = 200; // points
    private const double DefaultDigitalSignatureHeight = 56; // points

    private static readonly Brush DigitalPreviewFill = FrozenBrush(Color.FromArgb(0x30, 0x0F, 0x6C, 0xBD));
    private static readonly Brush DigitalPreviewStroke = FrozenBrush(Color.FromRgb(0x0F, 0x6C, 0xBD));

    private DigitalSignOptions? _pendingDigitalSign;
    private IReadOnlyList<SignatureInfoViewModel> _signatures = [];
    private bool _isCheckingSignatures;
    private int _signaturesGeneration;
    private IAsyncRelayCommand? _digitalSignCommand;
    private IRelayCommand<SignatureInfoViewModel>? _showCertificateCommand;
    private IRelayCommand<SignatureInfoViewModel>? _viewSignedVersionCommand;
    private IRelayCommand<SignatureInfoViewModel>? _goToSignatureCommand;

    /// <summary>Asks the view to show the Signatures panel.</summary>
    public event EventHandler? SignaturePanelRequested;

    public IAsyncRelayCommand DigitalSignCommand => _digitalSignCommand ??= new AsyncRelayCommand(() => StartDigitalSignAsync());

    public IRelayCommand<SignatureInfoViewModel> ShowCertificateCommand => _showCertificateCommand ??= new RelayCommand<SignatureInfoViewModel>(signature =>
    {
        if (signature?.Info.Certificate is { } certificate)
        {
            _dialogs.ShowCertificate(certificate);
        }
    });

    public IRelayCommand<SignatureInfoViewModel> ViewSignedVersionCommand => _viewSignedVersionCommand ??= new RelayCommand<SignatureInfoViewModel>(ViewSignedVersion);

    public IRelayCommand<SignatureInfoViewModel> GoToSignatureCommand => _goToSignatureCommand ??= new RelayCommand<SignatureInfoViewModel>(signature =>
    {
        if (signature?.Info.PageIndex is { } page && page < Pages.Count)
        {
            GoToPage(page);
        }
    });

    public IRelayCommand ShowSignaturePanelCommand => new RelayCommand(() => SignaturePanelRequested?.Invoke(this, EventArgs.Empty));

    public bool IsPlacingDigitalSignature => _pendingDigitalSign is not null;

    public IReadOnlyList<SignatureInfoViewModel> Signatures
    {
        get => _signatures;
        private set
        {
            _signatures = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSignatures));
            RaiseBannerChanged();
        }
    }

    public bool HasSignatures => _signatures.Count > 0;

    public bool ShowSignatureBanner => _isCheckingSignatures || HasSignatures;

    public string SignatureBannerText => Loc.T(
        _isCheckingSignatures ? "Checking signatures…"
        : _signatures.Any(s => s.Info.Status == PdfSignatureStatus.Invalid) ? "At least one signature is invalid."
        : _signatures.Any(s => s.Info.Status == PdfSignatureStatus.Unsupported) ? "Signed, but at least one signature can't be checked."
        : _signatures.Any(s => s.Info.Status != PdfSignatureStatus.Valid) ? (_signatures.Count == 1 ? "Signed, but the signer's identity couldn't be verified." : "Signed, but at least one signer's identity couldn't be verified.")
        : _signatures.Count == 1 ? "Signed and the signature is valid."
        : "Signed and all signatures are valid.");

    public SignatureInfoViewModel? BannerSample => _isCheckingSignatures ? null
        : _signatures.FirstOrDefault(s => s.Info.Status == PdfSignatureStatus.Invalid)
          ?? _signatures.FirstOrDefault(s => s.Info.Status != PdfSignatureStatus.Valid)
          ?? _signatures.FirstOrDefault();

    // --- Signing ------------------------------------------------------------------------------

    /// <param name="lockOnly">Right after placing a handwritten signature (see <see cref="SignatureDialogResult.LockWithDigitalId"/>).</param>
    private async Task StartDigitalSignAsync(bool lockOnly = false)
    {
        if (_document is null || IsBusy || !await EnsureAllowedAsync(Core.Pdf.Security.PdfPermissions.FillForms, "sign it"))
        {
            return;
        }

        CommitEditsRequested?.Invoke(this, EventArgs.Empty);
        EndPlacement();
        if (_dialogs.AskDigitalSign(SavedSignatures, lockOnly) is not { } options)
        {
            return;
        }

        if (!options.Visible)
        {
            await SignDigitallyAsync(options, null);
            return;
        }

        SelectItem(null);
        _pendingDigitalSign = options;
        OnPropertyChanged(nameof(IsPlacing));
        OnPropertyChanged(nameof(IsPlacingDigitalSignature));
        OnPropertyChanged(nameof(PlacementHint));
    }

    private void UpdateDigitalPlacementPreview(Gesture gesture, NormalizedPoint point) =>
        gesture.Page.SetPreview(new RectangleGeometry(ToPoints(gesture.Page, gesture.Start, point)), DigitalPreviewFill, DigitalPreviewStroke, 1);

    private async Task EndDigitalPlacementAsync(PageViewModel page, NormalizedPoint start, NormalizedPoint end, bool isClick)
    {
        if (_pendingDigitalSign is not { } options)
        {
            return;
        }

        NormalizedRect bounds;
        if (isClick)
        {
            // A click places a box of the default size centred on the point, kept inside the page.
            var width = Math.Min(1, DefaultDigitalSignatureWidth / page.WidthPoints);
            var height = Math.Min(1, DefaultDigitalSignatureHeight / page.HeightPoints);
            bounds = new NormalizedRect(
                Math.Clamp(end.X - (width / 2), 0, 1 - width),
                Math.Clamp(end.Y - (height / 2), 0, 1 - height),
                width,
                height);
        }
        else
        {
            bounds = Normalize(start, end);
            if (bounds.Width * page.WidthPoints < 20 || bounds.Height * page.HeightPoints < 10)
            {
                return; // Too small to read: keep placing.
            }
        }

        _pendingDigitalSign = null; // The options (and certificate) now belong to the signing step.
        RaisePlacementChanged();
        await SignDigitallyAsync(options, (page, bounds));
    }

    /// <summary>
    /// Saves pending edits, signs the result into a file the user picks, and opens the signed file.
    /// Nothing on disk changes unless signing succeeds.
    /// </summary>
    private async Task SignDigitallyAsync(DigitalSignOptions options, (PageViewModel Page, NormalizedRect Bounds)? placement)
    {
        using var certificate = options.Certificate;
        if (_document is not { } document || IsBusy)
        {
            return;
        }

        var targetPath = _dialogs.PickSavePdfFile(
            Path.GetFileNameWithoutExtension(document.FilePath) + " (signed).pdf", Path.GetDirectoryName(document.FilePath), "Save the signed document");
        if (targetPath is null)
        {
            return;
        }

        var saved = await TransformToFileAsync(
            targetPath,
            options.LongTermValidation ? "Signing and getting validation data…" : options.TimestampUrl is null ? "Signing…" : "Signing and getting a timestamp…",
            async (_, input, cancellationToken) =>
            {
                var signingTime = DateTimeOffset.Now;
                PdfSignatureAppearance? appearance = null;
                if (placement is { } p)
                {
                    var image = DigitalSignatureRenderer.Render(
                        p.Bounds.Width * p.Page.WidthPoints,
                        p.Bounds.Height * p.Page.HeightPoints,
                        certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, forIssuer: false),
                        signingTime,
                        options.Reason,
                        options.Handwritten);
                    appearance = new PdfSignatureAppearance(p.Page.Index, p.Bounds, ImageConversion.ToPdfImage(image));
                }

                var request = new PdfSignRequest(
                    certificate,
                    appearance,
                    options.Reason,
                    options.Location,
                    TimestampAuthority: options.TimestampUrl is { } url ? new HttpTimestampAuthority(url) : null,
                    SigningTime: signingTime,
                    Certification: options.Certification,
                    RevocationSource: options.LongTermValidation ? new HttpRevocationSource() : null);
                return await PdfSigner.SignAsync(input, request, cancellationToken);
            });
        if (saved is not null)
        {
            StatusMessage = Loc.F("Signed and saved {0}", Path.GetFileName(saved));
        }
    }

    private void CancelDigitalPlacement()
    {
        if (_pendingDigitalSign is { } options)
        {
            _pendingDigitalSign = null;
            options.Certificate.Dispose();
            RaisePlacementChanged();
        }
    }

    private void RaisePlacementChanged()
    {
        OnPropertyChanged(nameof(IsPlacing));
        OnPropertyChanged(nameof(IsPlacingDigitalSignature));
        OnPropertyChanged(nameof(PlacementHint));
    }

    // --- Verification -------------------------------------------------------------------------

    private async Task LoadSignaturesAsync(IPdfDocument document)
    {
        var generation = ++_signaturesGeneration;
        Signatures = [];
        IReadOnlyList<PdfSignatureInfo> results;
        try
        {
            // A quick pass without network access; it already tells most users what they need.
            results = await PdfSignatureVerifier.VerifyFileAsync(document.FilePath, new PdfVerifyOptions(CheckRevocation: false));
            if (generation != _signaturesGeneration || results.Count == 0)
            {
                return;
            }

            Signatures = results.Select(r => new SignatureInfoViewModel(r)).ToList();

            // Then ask the certificate authorities whether any certificate was revoked.
            if (results.Any(r => r.Identity == PdfSignerIdentity.Trusted))
            {
                SetChecking(true);
                results = await PdfSignatureVerifier.VerifyFileAsync(document.FilePath, new PdfVerifyOptions(CheckRevocation: true));
                if (generation == _signaturesGeneration)
                {
                    Signatures = results.Select(r => new SignatureInfoViewModel(r)).ToList();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PdfException or CryptographicException)
        {
            // Signatures can't be read; the document still opens.
        }
        finally
        {
            if (generation == _signaturesGeneration)
            {
                SetChecking(false);
            }
        }
    }

    private void ClearSignatures()
    {
        _signaturesGeneration++;
        _isCheckingSignatures = false;
        Signatures = [];
    }

    private void SetChecking(bool value)
    {
        _isCheckingSignatures = value;
        RaiseBannerChanged();
    }

    private void RaiseBannerChanged()
    {
        OnPropertyChanged(nameof(ShowSignatureBanner));
        OnPropertyChanged(nameof(SignatureBannerText));
        OnPropertyChanged(nameof(BannerSample));
    }

    /// <summary>Opens the bytes this signature covers in a second window, as they were when signed.</summary>
    private void ViewSignedVersion(SignatureInfoViewModel? signature)
    {
        if (signature is null || _document is not { } document)
        {
            return;
        }

        try
        {
            var revision = PdfSignatureVerifier.GetSignedRevision(File.ReadAllBytes(document.FilePath), signature.Info);
            var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "YRK PDF"));
            var path = Path.Combine(folder.FullName, $"{Path.GetFileNameWithoutExtension(document.FilePath)} (as signed by {Sanitize(signature.Info.SignerName)}).pdf");
            File.WriteAllBytes(path, revision);
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _dialogs.ShowError(Loc.F("The signed version could not be opened.\n\n{0}", ex.Message));
        }
    }

    private static string Sanitize(string name)
    {
        var clean = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return clean.Length > 0 ? clean : "signer";
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
