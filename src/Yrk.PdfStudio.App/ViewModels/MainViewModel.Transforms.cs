using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// Whole-file operations (digital signing, OCR, redaction): pending edits are saved first, the bytes
/// are processed, and the result is written to the chosen file and opened. Nothing on disk changes
/// unless the operation succeeds.
/// </summary>
public sealed partial class MainViewModel
{
    private CancellationTokenSource? _operation;
    private string _operationText = string.Empty;
    private IRelayCommand? _cancelOperationCommand;

    public bool IsOperationRunning => _operation is not null;

    public string OperationText
    {
        get => _operationText;
        private set => SetProperty(ref _operationText, Loc.T(value));
    }

    public IRelayCommand CancelOperationCommand => _cancelOperationCommand ??= new RelayCommand(() => _operation?.Cancel());

    /// <summary>
    /// Runs <paramref name="transform"/> on the current document's bytes (with pending edits) and opens the
    /// result from <paramref name="targetPath"/>. The transform returns null when there is nothing to write.
    /// </summary>
    /// <returns>The path written, or null when nothing was written.</returns>
    private async Task<string?> TransformToFileAsync(
        string targetPath,
        string busyText,
        Func<IPdfDocument, byte[], CancellationToken, Task<byte[]?>> transform)
    {
        busyText = Loc.T(busyText);
        if (_document is not { } document || IsBusy)
        {
            return null;
        }

        CommitEditsRequested?.Invoke(this, EventArgs.Empty);
        EndPlacement();
        IsBusy = true;
        _operation = new CancellationTokenSource();
        OnPropertyChanged(nameof(IsOperationRunning));
        OperationText = busyText;
        StatusMessage = busyText;
        var restore = CaptureViewState();
        var folder = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        var editsPath = Path.Combine(folder, $"~{Path.GetFileNameWithoutExtension(document.FilePath)}.{Guid.NewGuid():N}.yrk-saving");
        var tempPath = Path.Combine(folder, $"~{Path.GetFileNameWithoutExtension(targetPath)}.{Guid.NewGuid():N}.yrk-writing");
        try
        {
            try
            {
                byte[] input;
                if (IsDirty)
                {
                    var realText = CanWriteRealText;
                    var fieldLooks = BuildFieldLooks(document); // Before the fields are committed and reloaded.
                    await CommitOverlaysAsync(document, keepText: realText);
                    await document.SaveAsync(editsPath);
                    var saved = ApplyFieldLooks(await TrimSaveAsync(document.FilePath, await File.ReadAllBytesAsync(editsPath)), fieldLooks);
                    input = ApplyNewFields(ApplyLayerDefaults(ApplyTextEdits(realText ? ApplyPlacedText(saved) : saved)));
                }
                else
                {
                    input = await File.ReadAllBytesAsync(document.FilePath);
                }

                var output = await transform(document, input, _operation.Token);
                if (output is null)
                {
                    return null;
                }

                await File.WriteAllBytesAsync(tempPath, output);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                StatusMessage = "Cancelled";
                return null;
            }
            catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException or CryptographicException or HttpRequestException)
            {
                TryDelete(tempPath);
                _dialogs.ShowError(Loc.F("{0} failed.\n\n{1}", busyText.TrimEnd('…', '.'), ex.Message));
                StatusMessage = string.Empty;
                return null;
            }
            finally
            {
                TryDelete(editsPath);
            }

            await ReleaseDocumentAsync();
            var savedPath = targetPath;
            try
            {
                File.Move(tempPath, targetPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                savedPath = UniqueSiblingPath(targetPath, "new");
                File.Move(tempPath, savedPath);
                _dialogs.ShowError(Loc.F(
                    "Couldn't replace \"{0}\". Is it open in another program?\n\n{1}\n\nThe result was saved as \"{2}\" instead.",
                    Path.GetFileName(targetPath),
                    ex.Message,
                    Path.GetFileName(savedPath)));
            }

            ClearEdits();
            await OpenDocumentAsync(savedPath, _password, restore, keepEdits: false);
            return savedPath;
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            OnPropertyChanged(nameof(IsOperationRunning));
            if (StatusMessage == busyText || StatusMessage == OperationText)
            {
                StatusMessage = string.Empty;
            }

            OperationText = string.Empty;
            IsBusy = false;
        }
    }

    /// <summary>Reports progress of the running operation (shown above the page area with a Cancel button).</summary>
    private void ReportOperation(string text)
    {
        OperationText = text;
        StatusMessage = text;
    }
}
