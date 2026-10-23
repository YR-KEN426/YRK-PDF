using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>A colour offered for watermarks and header text.</summary>
public sealed record MarkColor(string Name, PdfColor Color)
{
    public Brush Brush { get; } = Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(Color.R, Color.G, Color.B)));

    public override string ToString() => Loc.T(Name);

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>A mark as drawn in the preview (page points, WPF angle).</summary>
public sealed record PreviewMark(string Text, double Left, double Top, double Angle, double FontSize, FontWeight Weight, Brush Brush, double Opacity, FontFamily Font);

/// <summary>The watermark, header and footer dialog, with a preview of one page.</summary>
public sealed partial class PageMarksViewModel : ObservableObject
{
    private static PageMarkSettings? _lastUsed;

    private readonly IPdfDocument _document;
    private readonly string _fileName;
    private int _previewPage;
    private BitmapSource? _previewImage;
    private bool _useWatermark = true;
    private bool _useHeaderFooter = true;
    private string _watermark = "CONFIDENTIAL";
    private bool _autoSize = true;
    private double _watermarkSize = 72;
    private MarkColor _watermarkColor;
    private double _opacityPercent = 30;
    private double _angle = 45;
    private bool _behind = true;
    private string _headerLeft = string.Empty;
    private string _headerCenter = string.Empty;
    private string _headerRight = string.Empty;
    private string _footerLeft = string.Empty;
    private string _footerCenter = "{page} / {pages}";
    private string _footerRight = string.Empty;
    private double _textSize = 10;
    private string _fontFamily = PageMarks.Font;
    private MarkColor _textColor;
    private double _marginMm = 12;
    private int _firstNumber = 1;
    private bool _skipFirstPage;

    public PageMarksViewModel(IPdfDocument document, string fileName, int currentPage)
    {
        _document = document;
        _fileName = fileName;
        _previewPage = currentPage;
        _watermarkColor = Colors[0];
        _textColor = Colors[3];
        if (_lastUsed is { } last)
        {
            Restore(last);
        }

        PreviousPageCommand = new RelayCommand(() => PreviewPage--, () => _previewPage > 0);
        NextPageCommand = new RelayCommand(() => PreviewPage++, () => _previewPage < _document.PageCount - 1);
        InsertCommand = new RelayCommand<string>(InsertPreset);
        _ = RenderPageAsync();
        Refresh();
    }

    public IReadOnlyList<MarkColor> Colors { get; } =
    [
        new("Grey", new PdfColor(128, 128, 128)),
        new("Red", new PdfColor(200, 30, 30)),
        new("Blue", new PdfColor(30, 70, 180)),
        new("Black", new PdfColor(0, 0, 0)),
    ];

    public IReadOnlyList<double> Angles { get; } = [45, 0, -45, 90];

    public IReadOnlyList<double> TextSizes => TextStyles.Sizes;

    public IReadOnlyList<string> Fonts => TextStyles.Fonts;

    /// <summary>The font of every mark; typing in the box applies only a whole installed font name.</summary>
    public string FontFamilyName
    {
        get => _fontFamily;
        set
        {
            if (TextStyles.Resolve(value) is { } family)
            {
                Set(ref _fontFamily, family);
            }
        }
    }

    /// <summary>Ready-made page numbers for the footer centre.</summary>
    public IReadOnlyList<string> NumberPresets { get; } = ["{page}", "{page} / {pages}", "Page {page} of {pages}", "หน้า {page} จาก {pages}", "- {page} -"];

    public bool UseWatermark { get => _useWatermark; set => Set(ref _useWatermark, value); }

    public bool UseHeaderFooter { get => _useHeaderFooter; set => Set(ref _useHeaderFooter, value); }

    public string Watermark { get => _watermark; set => Set(ref _watermark, value ?? string.Empty); }

    public bool AutoSize { get => _autoSize; set => Set(ref _autoSize, value); }

    public double WatermarkSize { get => _watermarkSize; set => Set(ref _watermarkSize, Math.Clamp(value, 6, 300)); }

    public MarkColor WatermarkColor { get => _watermarkColor; set => Set(ref _watermarkColor, value); }

    public double OpacityPercent { get => _opacityPercent; set => Set(ref _opacityPercent, Math.Clamp(Math.Round(value), 5, 100)); }

    public double Angle { get => _angle; set => Set(ref _angle, value); }

    public bool Behind { get => _behind; set => Set(ref _behind, value); }

    public string HeaderLeft { get => _headerLeft; set => Set(ref _headerLeft, value ?? string.Empty); }

    public string HeaderCenter { get => _headerCenter; set => Set(ref _headerCenter, value ?? string.Empty); }

    public string HeaderRight { get => _headerRight; set => Set(ref _headerRight, value ?? string.Empty); }

    public string FooterLeft { get => _footerLeft; set => Set(ref _footerLeft, value ?? string.Empty); }

    public string FooterCenter { get => _footerCenter; set => Set(ref _footerCenter, value ?? string.Empty); }

    public string FooterRight { get => _footerRight; set => Set(ref _footerRight, value ?? string.Empty); }

    public double TextSize { get => _textSize; set => Set(ref _textSize, value); }

    public MarkColor TextColor { get => _textColor; set => Set(ref _textColor, value); }

    public double MarginMm { get => _marginMm; set => Set(ref _marginMm, Math.Clamp(value, 0, 50)); }

    public int FirstNumber { get => _firstNumber; set => Set(ref _firstNumber, Math.Clamp(value, 0, 99999)); }

    public bool SkipFirstPage { get => _skipFirstPage; set => Set(ref _skipFirstPage, value); }

    public IRelayCommand PreviousPageCommand { get; }

    public IRelayCommand NextPageCommand { get; }

    /// <summary>Puts a ready-made page number into the footer centre.</summary>
    public IRelayCommand<string> InsertCommand { get; }

    public int PreviewPage
    {
        get => _previewPage;
        set
        {
            value = Math.Clamp(value, 0, _document.PageCount - 1);
            if (SetProperty(ref _previewPage, value))
            {
                OnPropertyChanged(nameof(PreviewText));
                PreviousPageCommand.NotifyCanExecuteChanged();
                NextPageCommand.NotifyCanExecuteChanged();
                _ = RenderPageAsync();
                Refresh();
            }
        }
    }

    public string PreviewText => Loc.F("Page {0} of {1}", _previewPage + 1, _document.PageCount);

    public double PageWidth => _document.PageSizes[_previewPage].Width;

    public double PageHeight => _document.PageSizes[_previewPage].Height;

    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set => SetProperty(ref _previewImage, value);
    }

    public ObservableCollection<PreviewMark> PreviewMarks { get; } = [];

    public bool CanApply => !Settings.IsEmpty;

    public PageMarkSettings Settings => new()
    {
        Watermark = _useWatermark ? _watermark : string.Empty,
        WatermarkSize = _autoSize ? 0 : _watermarkSize,
        WatermarkColor = _watermarkColor.Color,
        WatermarkOpacity = _opacityPercent / 100,
        WatermarkAngle = _angle,
        WatermarkBehind = _behind,
        HeaderLeft = _useHeaderFooter ? _headerLeft : string.Empty,
        HeaderCenter = _useHeaderFooter ? _headerCenter : string.Empty,
        HeaderRight = _useHeaderFooter ? _headerRight : string.Empty,
        FooterLeft = _useHeaderFooter ? _footerLeft : string.Empty,
        FooterCenter = _useHeaderFooter ? _footerCenter : string.Empty,
        FooterRight = _useHeaderFooter ? _footerRight : string.Empty,
        TextSize = _textSize,
        FontFamily = _fontFamily,
        TextColor = _textColor.Color,
        MarginMm = _marginMm,
        FirstNumber = _firstNumber,
        SkipFirstPage = _skipFirstPage,
    };

    /// <summary>Remembers the settings for the next time in this session.</summary>
    public PageMarkSettings Accept() => _lastUsed = Settings;

    private void Restore(PageMarkSettings s)
    {
        _useWatermark = s.Watermark.Length > 0;
        _watermark = s.Watermark.Length > 0 ? s.Watermark : _watermark;
        _autoSize = s.WatermarkSize <= 0;
        _watermarkSize = s.WatermarkSize > 0 ? s.WatermarkSize : _watermarkSize;
        _watermarkColor = Colors.FirstOrDefault(c => c.Color == s.WatermarkColor) ?? _watermarkColor;
        _opacityPercent = s.WatermarkOpacity * 100;
        _angle = s.WatermarkAngle;
        _behind = s.WatermarkBehind;
        (_headerLeft, _headerCenter, _headerRight) = (s.HeaderLeft, s.HeaderCenter, s.HeaderRight);
        (_footerLeft, _footerCenter, _footerRight) = (s.FooterLeft, s.FooterCenter, s.FooterRight);
        _useHeaderFooter = new[] { _headerLeft, _headerCenter, _headerRight, _footerLeft, _footerCenter, _footerRight }.Any(t => t.Length > 0);
        _textSize = s.TextSize;
        _fontFamily = s.FontFamily;
        _textColor = Colors.FirstOrDefault(c => c.Color == s.TextColor) ?? _textColor;
        _marginMm = s.MarginMm;
        _firstNumber = s.FirstNumber;
        _skipFirstPage = s.SkipFirstPage;
    }

    private void InsertPreset(string? preset)
    {
        if (preset is not null)
        {
            UseHeaderFooter = true;
            FooterCenter = preset;
        }
    }

    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name))
        {
            return false;
        }

        Refresh();
        return true;
    }

    private void Refresh()
    {
        PreviewMarks.Clear();
        var size = _document.PageSizes[_previewPage];
        var settings = Settings;
        foreach (var mark in PageMarks.Layout(settings, _previewPage, _document.PageCount, size.Width, size.Height, _fileName, DateTime.Today))
        {
            var brush = new SolidColorBrush(Color.FromRgb(mark.Color.R, mark.Color.G, mark.Color.B));
            brush.Freeze();
            PreviewMarks.Add(new PreviewMark(mark.Text, mark.Left, mark.Top, -mark.Angle, mark.FontSize, mark.Bold ? FontWeights.Bold : FontWeights.Normal, brush, mark.Opacity, new FontFamily(settings.FontFamily)));
        }

        OnPropertyChanged(nameof(PageWidth));
        OnPropertyChanged(nameof(PageHeight));
        OnPropertyChanged(nameof(CanApply));
    }

    private async Task RenderPageAsync()
    {
        var index = _previewPage;
        var size = _document.PageSizes[index];
        var scale = 900 / Math.Max(size.Width, size.Height);
        try
        {
            var bitmap = await _document.RenderPageAsync(index, Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
            if (index == _previewPage)
            {
                PreviewImage = BitmapFactory.Create(bitmap);
            }
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
        {
        }
    }
}
