using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>A type of item being counted ("Door", "Socket"), with its colour and how many there are.</summary>
public sealed class CountTypeViewModel : ObservableObject
{
    private string _name;
    private int _total;

    public CountTypeViewModel(string name, Color color)
    {
        _name = name;
        Color = color;
        Brush = new SolidColorBrush(color);
        Brush.Freeze();
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, string.IsNullOrWhiteSpace(value) ? _name : value.Trim()))
            {
                NameChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public Color Color { get; }

    public PdfColor PdfColor => new(Color.R, Color.G, Color.B);

    public SolidColorBrush Brush { get; }

    public int Total
    {
        get => _total;
        set => SetProperty(ref _total, value);
    }

    public event EventHandler? NameChanged;

    public override string ToString() => Name;
}

/// <summary>One counted item on a page: a numbered dot in its type's colour (position in PDF points).</summary>
public sealed class CountMarkViewModel : ObservableObject
{
    private int _number;

    public CountMarkViewModel(PageViewModel page, CountTypeViewModel type, NormalizedPoint position)
    {
        Page = page;
        Type = type;
        Position = position;
        Left = position.X * page.WidthPoints;
        Top = position.Y * page.HeightPoints;
    }

    public PageViewModel Page { get; }

    public CountTypeViewModel Type { get; }

    public NormalizedPoint Position { get; }

    public double Left { get; }

    public double Top { get; }

    public SolidColorBrush Brush => Type.Brush;

    /// <summary>Its number within its type, counted through the document page by page.</summary>
    public int Number
    {
        get => _number;
        set => SetProperty(ref _number, value);
    }
}
