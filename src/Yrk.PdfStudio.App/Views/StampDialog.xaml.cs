using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App.Views;

/// <summary>A stamp in the dialog's lists (public for data binding).</summary>
public sealed record StampTile(StampDesign Design, BitmapSource Image);

public partial class StampDialog : Window
{
    private static readonly StampShape[] Shapes = [StampShape.Box, StampShape.DoubleBox, StampShape.Oval];
    private static readonly StampDateStyle[] DateStyles =
        [StampDateStyle.None, StampDateStyle.ThaiShort, StampDateStyle.ThaiLong, StampDateStyle.ThaiNumeric, StampDateStyle.English];

    private readonly StampSettings _settings;
    private bool _loading = true;

    public StampDialog(StampSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        ShapeChoice.ItemsSource = new[] { Loc.T("Box"), Loc.T("Double border"), Loc.T("Oval") };
        DateInput.SelectedDate = DateTime.Today;
        FillDateChoices();
        PresetList.ItemsSource = RubberStamp.Presets.Select(Tile).ToList();
        FillMyStamps();

        Show(settings.Last ?? RubberStamp.Presets[0]);
        _loading = false;
        UpdatePreview();
        Loaded += (_, _) => WordsInput.Focus();
    }

    public StampRequest? Result { get; private set; }

    private DateTime Date => DateInput.SelectedDate ?? DateTime.Today;

    private StampDesign Current => new(
        WordsInput.Text.Trim(),
        DetailInput.Text.Replace("\r", string.Empty).Trim(),
        RedInk.IsChecked == true ? StampInk.Red
            : BlueInk.IsChecked == true ? StampInk.Blue
            : GreenInk.IsChecked == true ? StampInk.Green
            : StampInk.Black,
        Shapes[Math.Max(0, ShapeChoice.SelectedIndex)],
        DateStyles[Math.Max(0, DateChoice.SelectedIndex)]);

    private static StampTile Tile(StampDesign design) => new(design, RubberStamp.Render(design, DateTime.Today, pixelsPerPoint: 2).Image);

    private void FillMyStamps()
    {
        MyList.ItemsSource = _settings.MyStamps.Select(Tile).ToList();
        NoMyStamps.Visibility = _settings.MyStamps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The date choices show the chosen date in each style.</summary>
    private void FillDateChoices()
    {
        var index = DateChoice.SelectedIndex;
        DateChoice.ItemsSource = DateStyles.Select(s => RubberStamp.FormatDate(Date, s) ?? Loc.T("No date")).ToList();
        DateChoice.SelectedIndex = index;
    }

    private void Show(StampDesign design)
    {
        var loading = _loading;
        _loading = true;
        WordsInput.Text = design.Text;
        DetailInput.Text = design.Detail ?? string.Empty;
        (design.Ink switch
        {
            StampInk.Red => RedInk,
            StampInk.Blue => BlueInk,
            StampInk.Green => GreenInk,
            _ => BlackInk,
        }).IsChecked = true;
        ShapeChoice.SelectedIndex = Array.IndexOf(Shapes, design.Shape);
        DateChoice.SelectedIndex = Array.IndexOf(DateStyles, design.Date);
        _loading = loading;
        UpdatePreview();
    }

    private void OnStampChosen(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: StampTile tile } list)
        {
            return;
        }

        // One selection across both lists.
        (list == PresetList ? MyList : PresetList).SelectedItem = null;
        Show(tile.Design);
    }

    private void OnDesignChanged(object sender, RoutedEventArgs e)
    {
        if (sender == DateInput && !_loading)
        {
            _loading = true;
            FillDateChoices();
            _loading = false;
        }

        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_loading)
        {
            return;
        }

        var design = Current;
        var empty = design.Text.Length == 0 && !design.DetailLines.Any();
        Preview.Source = empty ? null : RubberStamp.Render(design, Date, pixelsPerPoint: 2).Image;
        PlaceButton.IsEnabled = SaveButton.IsEnabled = !empty;
        DeleteButton.IsEnabled = _settings.MyStamps.Contains(design);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var design = Current;
        if (!_settings.MyStamps.Contains(design))
        {
            _settings.MyStamps.Add(design);
            _settings.Save();
            FillMyStamps();
        }

        MyList.SelectedIndex = _settings.MyStamps.IndexOf(design);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_settings.MyStamps.Remove(Current))
        {
            _settings.Save();
            FillMyStamps();
            UpdatePreview();
        }
    }

    private void OnPlace(object sender, RoutedEventArgs e)
    {
        _settings.Last = Current;
        _settings.Save();
        Result = new StampRequest(Current, Date);
        DialogResult = true;
    }
}
