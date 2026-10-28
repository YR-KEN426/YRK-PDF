using System.IO;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Security;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// Password protection (AES-256) and the restrictions other people put on their documents: printing,
/// copying and changes are only allowed when the file permits them, unless the owner password is entered.
/// </summary>
public sealed partial class MainViewModel
{
    private IAsyncRelayCommand? _protectCommand;
    private IAsyncRelayCommand? _removeProtectionCommand;

    public IAsyncRelayCommand ProtectCommand => _protectCommand ??= new AsyncRelayCommand(ProtectAsync);

    public IAsyncRelayCommand RemoveProtectionCommand => _removeProtectionCommand ??= new AsyncRelayCommand(RemoveProtectionAsync);

    public bool IsProtected => _document?.IsEncrypted == true;

    /// <summary>Only form entries need "fill in forms"; any other change needs "change the content".</summary>
    private PdfPermissions SavePermission =>
        _placedItems.Count == 0 && !HasTextEdits && !_documentModified && !_layerDefaultsChanged
            ? PdfPermissions.FillForms
            : PdfPermissions.Modify;

    private async Task ProtectAsync()
    {
        if (_document is not { } document || IsBusy
            || !await EnsureAllowedAsync(PdfPermissions.All, "change its security")
            || _dialogs.AskProtection() is not { } protection)
        {
            return;
        }

        var targetPath = _dialogs.PickSavePdfFile(
            Path.GetFileNameWithoutExtension(document.FilePath) + " (protected).pdf", Path.GetDirectoryName(document.FilePath), "Save the protected document");
        if (targetPath is null)
        {
            return;
        }

        var saved = await TransformToFileAsync(targetPath, "Protecting…", async (doc, input, cancellationToken) =>
        {
            var plain = PdfFiles.IsEncrypted(input) ? await doc.SaveDecryptedAsync(cancellationToken) : input;
            var protectedBytes = await Task.Run(() => PdfSecurity.Protect(plain, protection), cancellationToken);

            // Reopen as the owner, who keeps every right.
            _password = protection.OwnerPassword ?? protection.UserPassword;
            return protectedBytes;
        });
        if (saved is not null)
        {
            StatusMessage = Loc.F("Protected and saved {0}", Path.GetFileName(saved));
        }
    }

    private async Task RemoveProtectionAsync()
    {
        if (_document is not { } document || IsBusy)
        {
            return;
        }

        if (!document.IsEncrypted)
        {
            _dialogs.ShowInfo("This document is not password protected.");
            return;
        }

        if (!await EnsureAllowedAsync(PdfPermissions.All, "remove its protection"))
        {
            return;
        }

        var targetPath = _dialogs.PickSavePdfFile(
            Path.GetFileNameWithoutExtension(document.FilePath) + " (unprotected).pdf", Path.GetDirectoryName(document.FilePath), "Save without protection");
        if (targetPath is null)
        {
            return;
        }

        var saved = await TransformToFileAsync(targetPath, "Removing protection…", async (doc, _, cancellationToken) =>
        {
            var plain = await doc.SaveDecryptedAsync(cancellationToken);
            _password = null;
            return plain;
        });
        if (saved is not null)
        {
            StatusMessage = Loc.F("Saved {0} without a password", Path.GetFileName(saved));
        }
    }

    /// <summary>
    /// True when the open document allows <paramref name="needed"/>. Otherwise explains what the author
    /// restricted and asks for the owner password; with the right one the document is reopened as owner.
    /// </summary>
    /// <param name="action">What the user wants to do, completing "doesn't allow you to …" (English, translated here).</param>
    private async Task<bool> EnsureAllowedAsync(PdfPermissions needed, string action)
    {
        if (_document is not { } document || (document.Permissions & needed) == needed)
        {
            return true;
        }

        var name = Path.GetFileName(document.FilePath);
        var reason = Loc.F("The author of \"{0}\" doesn't allow you to {1}. Enter the owner password to unlock it.", name, Loc.T(action));
        var failed = false;
        while (_dialogs.AskOwnerPassword(name, reason, failed) is { } password)
        {
            try
            {
                await using (var check = await _engine.OpenAsync(document.FilePath, password))
                {
                    if (check.Permissions != PdfPermissions.All)
                    {
                        failed = true; // It opens the file, but it is not the owner password.
                        continue;
                    }
                }

                await OpenDocumentAsync(document.FilePath, password, CaptureViewState(), keepEdits: true, copyPath: _workingCopy);
                return _document?.Permissions == PdfPermissions.All;
            }
            catch (PdfPasswordRequiredException)
            {
                failed = true;
            }
            catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
            {
                _dialogs.ShowError(ex.Message);
                return false;
            }
        }

        return false;
    }
}
