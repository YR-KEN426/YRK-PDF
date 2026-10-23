using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Yrk.PdfStudio.Core.Pdf.Layers;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>One row of the Layers panel: a layer with a check box, or a heading that switches its group.</summary>
public sealed class LayerViewModel(PdfLayer layer, Action<LayerViewModel> changed) : ObservableObject
{
    private bool _isVisible = layer.IsVisibleByDefault;

    public PdfLayer Layer { get; } = layer;

    public string Name => Layer.Name;

    public bool IsHeading => Layer.IsHeading;

    /// <summary>Locked layers cannot be switched (the author's choice, as in Acrobat).</summary>
    public bool CanToggle => !Layer.IsLocked;

    public Thickness Indent => new(Layer.Depth * 14, 0, 0, 0);

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value))
            {
                changed(this);
            }
        }
    }

    /// <summary>Sets the state without reporting it (used when a whole group is switched).</summary>
    internal void SetQuietly(bool visible) => SetProperty(ref _isVisible, visible, nameof(IsVisible));
}
