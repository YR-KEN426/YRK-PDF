using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Layers;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// The Layers panel: drawings exported from AutoCAD, Illustrator and similar programs keep their layers
/// (optional content), which can be shown and hidden here without changing the file.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _layerDefaultsChanged;
    private bool _updatingLayers;
    private int _layersGeneration;
    private IRelayCommand? _showAllLayersCommand;
    private IRelayCommand? _hideAllLayersCommand;
    private IRelayCommand? _resetLayersCommand;
    private IRelayCommand? _saveLayerDefaultsCommand;

    public ObservableCollection<LayerViewModel> Layers { get; } = [];

    public bool HasLayers => Layers.Count > 0;

    public IRelayCommand ShowAllLayersCommand => _showAllLayersCommand ??= new RelayCommand(() => SetAllLayers(true));

    public IRelayCommand HideAllLayersCommand => _hideAllLayersCommand ??= new RelayCommand(() => SetAllLayers(false));

    public IRelayCommand ResetLayersCommand => _resetLayersCommand ??= new RelayCommand(ResetLayers);

    /// <summary>Makes the current visibility the one the file opens with (applied when the document is saved).</summary>
    public IRelayCommand SaveLayerDefaultsCommand => _saveLayerDefaultsCommand ??= new RelayCommand(() =>
    {
        _layerDefaultsChanged = true;
        OnEditsChanged();
        StatusMessage = "The layer visibility will be saved with the document.";
    });

    private async Task LoadLayersAsync(IPdfDocument document)
    {
        var generation = ++_layersGeneration;
        IReadOnlyList<PdfLayer> layers;
        try
        {
            layers = await document.GetLayersAsync();
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
        {
            layers = [];
        }

        if (generation != _layersGeneration)
        {
            return;
        }

        Layers.Clear();
        foreach (var layer in layers)
        {
            Layers.Add(new LayerViewModel(layer, OnLayerToggled));
        }

        OnPropertyChanged(nameof(HasLayers));
    }

    private void ClearLayers()
    {
        _layersGeneration++;
        _layerDefaultsChanged = false;
        Layers.Clear();
        OnPropertyChanged(nameof(HasLayers));
    }

    private void OnLayerToggled(LayerViewModel layer)
    {
        if (_updatingLayers)
        {
            return;
        }

        // A heading switches every layer below it.
        if (layer.IsHeading)
        {
            var index = Layers.IndexOf(layer);
            for (var i = index + 1; i < Layers.Count && Layers[i].Layer.Depth > layer.Layer.Depth; i++)
            {
                if (Layers[i].CanToggle)
                {
                    Layers[i].SetQuietly(layer.IsVisible);
                }
            }
        }

        _ = ApplyLayersAsync();
    }

    private void SetAllLayers(bool visible)
    {
        _updatingLayers = true;
        foreach (var layer in Layers.Where(l => l.CanToggle))
        {
            layer.SetQuietly(visible);
        }

        _updatingLayers = false;
        _ = ApplyLayersAsync();
    }

    private void ResetLayers()
    {
        _updatingLayers = true;
        foreach (var layer in Layers)
        {
            layer.SetQuietly(layer.Layer.IsVisibleByDefault);
        }

        _updatingLayers = false;
        _ = ApplyLayersAsync(useDefault: true);
    }

    private async Task ApplyLayersAsync(bool useDefault = false)
    {
        if (_document is not { } document)
        {
            return;
        }

        var matchesDefault = Layers.All(l => l.IsHeading || l.IsVisible == l.Layer.IsVisibleByDefault);
        try
        {
            await document.SetHiddenLayersAsync(useDefault || matchesDefault ? null : HiddenLayerIds());
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
        {
            return;
        }

        if (document != _document)
        {
            return;
        }

        foreach (var page in Pages)
        {
            page.ReleaseImage();
            if (page.Thumbnail is not null)
            {
                ReleaseThumbnail(page);
                RequestThumbnail(page);
            }
        }

        UpdateVisiblePages();
    }

    private HashSet<string> HiddenLayerIds() =>
        Layers.Where(l => !l.IsHeading && !l.IsVisible).Select(l => l.Layer.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>Applies "save layer visibility" to the saved bytes.</summary>
    private byte[] ApplyLayerDefaults(byte[] pdf) =>
        _layerDefaultsChanged ? PdfLayerReader.WithVisibility(pdf, HiddenLayerIds()) : pdf;
}
