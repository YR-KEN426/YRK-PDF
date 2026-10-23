using CommunityToolkit.Mvvm.ComponentModel;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>A search highlight in a 1000×1000 page-relative coordinate space, so zooming never recomputes it.</summary>
public sealed class HighlightViewModel : ObservableObject
{
    public const double Space = 1000;

    private bool _isCurrent;

    public HighlightViewModel(NormalizedRect rect)
    {
        X = rect.X * Space;
        Y = rect.Y * Space;
        Width = rect.Width * Space;
        Height = rect.Height * Space;
    }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}
