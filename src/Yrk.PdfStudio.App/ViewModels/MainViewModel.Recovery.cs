using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// Crash recovery: while the document has unsaved changes, a copy with them is written every minute (see
/// <see cref="RecoveryStore"/>). The open document is not touched: it is saved to a new file, and the items not yet
/// written into it (signatures, stamps, added text, form values) are drawn onto that copy as pictures.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromMinutes(1);

    private readonly string _recoveryId = Guid.NewGuid().ToString("N");
    private DispatcherTimer? _recoveryTimer;
    private int _editsVersion;
    private int _recoveredVersion = -1;
    private bool _writingRecovery;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsDirty))
        {
            OnDirtyChanged();
        }
    }

    private void OnDirtyChanged()
    {
        _editsVersion++;
        if (IsDirty)
        {
            _recoveryTimer ??= Application.Current?.Dispatcher is { } dispatcher
                ? new DispatcherTimer(RecoveryInterval, DispatcherPriority.Background, async (_, _) => await WriteRecoveryAsync(), dispatcher)
                : null;
        }
        else if (_recoveredVersion >= 0)
        {
            // Saved or discarded: the copy is no longer needed.
            RecoveryStore.Delete(_recoveryId);
            _recoveredVersion = -1;
        }
    }

    /// <summary>Writes the recovery copy when something changed since the last one.</summary>
    internal async Task WriteRecoveryAsync()
    {
        if (_writingRecovery || IsBusy || _document is not { } document || !IsDirty || _recoveredVersion == _editsVersion
            || DocumentPath is not { } original)
        {
            return;
        }

        _writingRecovery = true;
        var version = _editsVersion;
        var first = Path.Combine(RecoveryStore.Folder, _recoveryId + ".tmp");
        var second = Path.Combine(RecoveryStore.Folder, _recoveryId + ".tmp2");
        try
        {
            Directory.CreateDirectory(RecoveryStore.Folder);
            await document.SaveAsync(first);
            var items = _placedItems.ToList();
            var fields = _fieldEdits.Values.ToList();
            if (items.Count > 0 || fields.Count > 0)
            {
                await using (var copy = await _engine.OpenAsync(first, _password))
                {
                    foreach (var item in items.Where(i => !i.IsText || i.Text.Trim().Length > 0))
                    {
                        await copy.AddImageAsync(item.PageIndex, ItemRenderer.Render(item), item.Bounds);
                    }

                    if (fields.Count > 0)
                    {
                        await copy.ApplyFormEditsAsync(fields);
                    }

                    await copy.SaveAsync(second);
                }

                File.Move(second, first, overwrite: true);
            }

            RecoveryStore.Keep(_recoveryId, first, original, Path.GetFileName(original));
            _recoveredVersion = version;
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // Only a safety net: try again at the next tick.
        }
        finally
        {
            _writingRecovery = false;
            try
            {
                File.Delete(second);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        // Saved or discarded while the copy was being written.
        if (!IsDirty)
        {
            RecoveryStore.Delete(_recoveryId);
            _recoveredVersion = -1;
        }
    }

    /// <summary>The tab is closing with its changes saved or discarded.</summary>
    internal void ForgetRecovery()
    {
        _recoveryTimer?.Stop();
        RecoveryStore.Delete(_recoveryId);
        _recoveredVersion = -1;
    }
}
