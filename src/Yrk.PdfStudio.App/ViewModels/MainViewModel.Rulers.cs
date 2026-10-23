using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Measure;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>Rulers along the page view, in the same units and scale as the measure tools.</summary>
public sealed partial class MainViewModel
{
    private bool _showRulers;
    private string _rulerUnit = "mm";
    private string _rulerUnitTip = string.Empty;
    private IRelayCommand? _toggleRulersCommand;

    public bool ShowRulers
    {
        get => _showRulers;
        set => SetProperty(ref _showRulers, value);
    }

    public IRelayCommand ToggleRulersCommand => _toggleRulersCommand ??= new RelayCommand(() => ShowRulers = !ShowRulers, () => HasDocument);

    /// <summary>The unit shown in the rulers' corner.</summary>
    public string RulerUnit
    {
        get => _rulerUnit;
        private set => SetProperty(ref _rulerUnit, value);
    }

    public string RulerUnitTip
    {
        get => _rulerUnitTip;
        private set => SetProperty(ref _rulerUnitTip, value);
    }

    /// <summary>
    /// Screen DIPs per ruler unit for a page at the current zoom, and the unit's name: the measure scale
    /// (from the drawing, a preset or a calibration) in the chosen unit.
    /// </summary>
    public async Task<(double DipsPerUnit, string Unit)> GetRulerScaleAsync(PageViewModel page)
    {
        var (perPoint, unit, scaleName) = await ScaleAtAsync(page, new NormalizedPoint(0.5, 0.5));
        var unitName = unit ?? _measureUnit;
        var unitsPerPoint = unit is null ? perPoint / Measurement.Millimetres(_measureUnit)!.Value : perPoint;
        RulerUnit = unitName;
        RulerUnitTip = Loc.F("Rulers in {0} ({1}). Click to change the unit or scale.", unitName, Loc.T(scaleName));
        return (PointsToDips * _zoom / unitsPerPoint, unitName);
    }
}
