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
    private DateTime _passwordEnteredAt = DateTime.MinValue;
    private string? _editConfirmedFor;

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

    /// <summary>The first change to a protected file: its password is asked before any work is done, once per file.</summary>
    private bool ConfirmFirstEdit()
    {
        if (_document is not { IsEncrypted: true } document || _editConfirmedFor == document.FilePath)
        {
            return true;
        }

        if (!ConfirmPassword(PdfPermissions.None, "edit it", atSave: false).Confirmed)
        {
            return false;
        }

        _editConfirmedFor = document.FilePath;
        return true;
    }

    /// <summary>Tools whose use changes the file, as opposed to reading or measuring it.</summary>
    private static bool ChangesTheFile(AnnotationTool tool) =>
        tool is AnnotationTool.Highlight or AnnotationTool.Underline or AnnotationTool.StrikeOut or AnnotationTool.Note
            or AnnotationTool.Pen or AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Arrow or AnnotationTool.Line
            or AnnotationTool.EditText;

    /// <summary>
    /// Before a change to a file with an open password is written: asks for the password again, so nobody can
    /// change a file left open on an unattended computer. A file without an open password is not asked about.
    /// </summary>
    /// <returns>Whether to go on, and the password to write the file with (null for an unprotected file).</returns>
    private (bool Confirmed, string? Password) ConfirmPassword(PdfPermissions needed, string action, bool atSave = true)
    {
        if (_document is not { IsEncrypted: true } document)
        {
            return (true, null);
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(document.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(ex.Message);
            return (false, null);
        }

        // Restrictions only (anyone can open it): the password it was opened with, as unlocked before. And not twice
        // in a row: the owner password typed a moment ago to lift the restrictions is confirmation enough.
        if (PdfSecurity.CheckPassword(bytes, null, PdfPermissions.None) == PdfUnlockResult.Unlocked
            || (atSave && DateTime.UtcNow - _passwordEnteredAt < TimeSpan.FromMinutes(1)
                && PdfSecurity.CheckPassword(bytes, document.OpenPassword, needed) == PdfUnlockResult.Unlocked))
        {
            return (true, document.OpenPassword);
        }

        var name = Path.GetFileName(document.FilePath);
        var message = Loc.F("\"{0}\" is protected with a password. Enter it again to {1}.", name, Loc.T(action));
        var failed = false;
        while (_dialogs.ConfirmPassword(name, message, failed) is { } password)
        {
            switch (PdfSecurity.CheckPassword(bytes, password, needed))
            {
                case PdfUnlockResult.Unlocked:
                    return (true, password);
                case PdfUnlockResult.Restricted:
                    message = Loc.F("The author of \"{0}\" doesn't allow you to {1}. Enter the owner password to unlock it.", name, Loc.T(action));
                    failed = false;
                    break;
                default:
                    failed = true;
                    break;
            }
        }

        return (false, null);
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

                // Page changes made in memory (turned, moved or deleted pages) exist only in the open document:
                // a copy carries them into the document reopened with the owner password.
                var copy = _workingCopy;
                if (_documentModified)
                {
                    Directory.CreateDirectory(WorkingFolder);
                    copy = Path.Combine(WorkingFolder, $"unlocked-{Guid.NewGuid():N}.pdf");
                    await document.SaveAsync(copy);
                }

                if (!await OpenDocumentAsync(document.FilePath, password, CaptureViewState(), keepEdits: true, copyPath: copy) && copy != _workingCopy)
                {
                    TryDelete(copy!);
                }

                _passwordEnteredAt = DateTime.UtcNow;
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
