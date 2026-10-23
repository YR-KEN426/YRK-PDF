using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Archive;
using Yrk.PdfStudio.Core.Pdf.Compression;
using Yrk.PdfStudio.Core.Pdf.Security;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// Tools ▸ Process many files: one job (PDF/A, compress, marks, password, export) for a list of files or a
/// folder. Each result is a new file in the chosen folder; the originals are never changed. A file that
/// fails is skipped with its reason, and the rest carry on.
/// </summary>
public sealed partial class MainViewModel
{
    private IAsyncRelayCommand? _batchCommand;

    public IAsyncRelayCommand BatchCommand => _batchCommand ??= new AsyncRelayCommand(BatchAsync);

    private async Task BatchAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var initial = _document is { } open ? [_convertedFrom is null ? open.FilePath : string.Empty] : Array.Empty<string>();
        if (_dialogs.AskBatch(initial.Where(p => p.Length > 0)) is not { } request)
        {
            return;
        }

        // The job's own settings, asked once with the usual dialogs.
        PdfCompressionOptions? compression = null;
        Imaging.PageMarkSettings? marks = null;
        PdfProtection? protection = null;
        ExportSettings? export = null;
        switch (request.Action)
        {
            case BatchAction.Compress:
                compression = _dialogs.AskCompression();
                if (compression is null)
                {
                    return;
                }

                break;
            case BatchAction.PageMarks:
                marks = await AskBatchMarksAsync(request.Files);
                if (marks is null)
                {
                    return;
                }

                break;
            case BatchAction.Protect:
                protection = _dialogs.AskProtection();
                if (protection is null)
                {
                    return;
                }

                break;
            case BatchAction.Export:
                export = _dialogs.AskExport(pageCount: 1, currentPage: 0, batch: true);
                if (export is null)
                {
                    return;
                }

                break;
        }

        try
        {
            Directory.CreateDirectory(request.OutputFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _dialogs.ShowError(Loc.F("Couldn't use the folder \"{0}\".\n\n{1}", request.OutputFolder, ex.Message));
            return;
        }

        IsBusy = true;
        _operation = new CancellationTokenSource();
        OnPropertyChanged(nameof(IsOperationRunning));
        var done = new List<string>();
        var failed = new List<(string File, string Reason)>();
        var cancelled = false;
        var succeeded = 0;
        try
        {
            for (var i = 0; i < request.Files.Count; i++)
            {
                if (_operation.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var file = request.Files[i];
                ReportOperation(Loc.F("File {0} of {1}: {2}", i + 1, request.Files.Count, Path.GetFileName(file)));
                try
                {
                    done.AddRange(await ProcessOneAsync(file, request, compression, marks, protection, export, _operation.Token));
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (PdfAException ex)
                {
                    failed.Add((file, Loc.T("Cannot be made PDF/A without changing how it looks") + ": " + Describe(ex.Problems).Replace("\n", " ", StringComparison.Ordinal)));
                }
                catch (PdfPasswordRequiredException)
                {
                    failed.Add((file, Loc.T("Needs a password to open")));
                }
                catch (BatchSkipException ex)
                {
                    failed.Add((file, Loc.T(ex.Message)));
                }
                catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
                {
                    failed.Add((file, ex.Message));
                }
            }
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            OnPropertyChanged(nameof(IsOperationRunning));
            OperationText = string.Empty;
            IsBusy = false;
        }

        StatusMessage = Loc.F("Processed {0} of {1} files", succeeded, request.Files.Count);
        var summary = Loc.F("Done: {0} of {1} file(s). The results are in \"{2}\".", succeeded, request.Files.Count, request.OutputFolder);
        if (cancelled)
        {
            summary += "\n" + Loc.T("Stopped before the end, as you asked.");
        }

        if (failed.Count > 0)
        {
            summary += "\n\n" + Loc.F("Not done ({0}):", failed.Count) + "\n" + string.Join("\n", failed.Take(15).Select(f => $"• {Path.GetFileName(f.File)}: {f.Reason}"));
            if (failed.Count > 15)
            {
                summary += "\n" + Loc.F("…and {0} more", failed.Count - 15);
            }
        }

        if (done.Count > 0 && _dialogs.AskYesNo(summary + "\n\n" + Loc.T("Open the folder with the results?")))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{request.OutputFolder}\"") { UseShellExecute = false });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _dialogs.ShowError(ex.Message);
            }
        }
        else if (done.Count == 0)
        {
            _dialogs.ShowError(summary);
        }
    }

    /// <summary>The marks dialog needs a document for its preview: the first file that opens.</summary>
    private async Task<Imaging.PageMarkSettings?> AskBatchMarksAsync(IReadOnlyList<string> files)
    {
        foreach (var file in files.Take(10))
        {
            try
            {
                await using var preview = await _engine.OpenAsync(file);
                return _dialogs.AskPageMarks(new PageMarksViewModel(preview, Path.GetFileName(file), 0));
            }
            catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
            {
                // Try the next file for the preview.
            }
        }

        _dialogs.ShowError("None of the first files could be opened for the preview.");
        return null;
    }

    private async Task<IReadOnlyList<string>> ProcessOneAsync(
        string file,
        BatchRequest request,
        PdfCompressionOptions? compression,
        Imaging.PageMarkSettings? marks,
        PdfProtection? protection,
        ExportSettings? export,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        string Target(string suffix, string extension) => UniquePath(Path.Combine(request.OutputFolder, name + suffix + extension), file);

        if (request.Action == BatchAction.Compress)
        {
            var output = Target(" (compressed)", ".pdf");
            await PdfCompressor.CompressAsync(file, null, output, compression!, new WpfImageCodec(), cancellationToken);
            return [output];
        }

        await using var document = await _engine.OpenAsync(file, cancellationToken: cancellationToken);
        var needed = request.Action == BatchAction.Export
            ? export!.IsPicture ? PdfPermissions.Print : PdfPermissions.Copy
            : PdfPermissions.All;
        if ((document.Permissions & needed) != needed)
        {
            throw new BatchSkipException("Protected against this change (needs the owner password)");
        }

        switch (request.Action)
        {
            case BatchAction.PdfA:
            {
                var bytes = await document.SaveDecryptedAsync(cancellationToken);
                var result = await Task.Run(() => PdfA.Convert(bytes, new WpfPdfAFontSource(), name), cancellationToken);
                var output = Target(" (PDF-A)", ".pdf");
                await File.WriteAllBytesAsync(output, result.Pdf, cancellationToken);
                return [output];
            }

            case BatchAction.PageMarks:
            {
                var bytes = await document.SaveDecryptedAsync(cancellationToken);
                var pages = Enumerable.Range(0, document.PageCount).ToList();
                var blocks = Imaging.PageMarks.ToBlocks(marks!, document.PageSizes, pages, Path.GetFileName(file), DateTime.Today); // WPF shaping: this thread.
                var marked = await Task.Run(() => PdfFiles.Compact(PdfTextWriter.AddText(bytes, blocks)), cancellationToken);
                var output = Target(" (marked)", ".pdf");
                await File.WriteAllBytesAsync(output, marked, cancellationToken);
                return [output];
            }

            case BatchAction.Protect:
            {
                var bytes = await document.SaveDecryptedAsync(cancellationToken);
                var locked = await Task.Run(() => PdfSecurity.Protect(bytes, protection!), cancellationToken);
                var output = Target(" (protected)", ".pdf");
                await File.WriteAllBytesAsync(output, locked, cancellationToken);
                return [output];
            }

            default:
            {
                var settings = export! with { Pages = Enumerable.Range(0, document.PageCount).ToList() };
                var output = Target(string.Empty, settings.Extension);
                var result = await PdfExporter.ExportAsync(document, settings, output, new Progress<int>(), cancellationToken);
                return result.Files;
            }
        }
    }

    /// <summary>A new file name in the output folder: never the source, never an existing file.</summary>
    internal static string UniquePath(string path, string source)
    {
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var candidate = path;
        for (var n = 2; File.Exists(candidate) || string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase); n++)
        {
            candidate = Path.Combine(folder, $"{name} ({n}){extension}");
        }

        return candidate;
    }

    /// <summary>A file the job cannot do; the message is shown in the summary.</summary>
    private sealed class BatchSkipException(string message) : Exception(message);
}
