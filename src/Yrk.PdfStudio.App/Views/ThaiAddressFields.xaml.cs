using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Yrk.PdfStudio.Core.Pdf.ETax;

namespace Yrk.PdfStudio.App.Views;

public partial class ThaiAddressFields : UserControl
{
    private bool _busy = true;
    private string? _lastAutoFilled;
    private string? _optionsSubdistrict;
    private string? _optionsLanguage;
    private ThaiAddressNarrowing? _narrowing;
    private ThaiAddressAutoSelection? _autoSelectedAreas;
    private ThaiAddressNotice _notice;

    public ThaiAddressFields()
    {
        InitializeComponent();
        ProvinceInput.ItemsSource = ThaiAreas.Provinces;
        PostcodeInput.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(OnPostcodeChanged));
        Loaded += (_, _) => { Loc.LanguageChanged += OnLanguageChanged; RefreshText(); };
        Unloaded += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;
        _busy = false;
    }

    public event EventHandler? Changed;
    public string BuildingNumber => HouseInput.Text.Trim();
    public string Address => StreetInput.Text.Trim();
    public string Postcode => PostcodeInput.Text.Trim();
    public string? Province => (ProvinceInput.SelectedItem as ThaiArea)?.Code;
    public string? District => (DistrictInput.SelectedItem as ThaiArea)?.Code;
    public string? Subdistrict => (SubdistrictInput.SelectedItem as ThaiArea)?.Code;

    // Restore all fields as one operation. Saved postcodes are user data, never suggestions.
    public void Restore(TaxParty party)
    {
        _busy = true;
        _narrowing = null;
        _autoSelectedAreas = null;
        _lastAutoFilled = null;
        _notice = ThaiAddressNotice.None;
        HouseInput.Text = party.BuildingNumber ?? string.Empty;
        StreetInput.Text = party.Address;
        ProvinceInput.ItemsSource = ThaiAreas.Provinces;
        Select(ProvinceInput, party.Province);
        DistrictInput.ItemsSource = Province is { } province ? ThaiAreas.DistrictsOf(province) : [];
        Select(DistrictInput, party.District);
        SubdistrictInput.ItemsSource = District is { } district ? ThaiAreas.SubdistrictsOf(district) : [];
        Select(SubdistrictInput, party.Subdistrict);
        RefreshOptions();
        PostcodeInput.Text = party.Postcode;
        _busy = false;
        CheckNotice();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void Select(ComboBox input, string? code) =>
        input.SelectedItem = input.Items.OfType<ThaiArea>().FirstOrDefault(a => a.Code == code);

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_busy) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnProvinceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        DistrictInput.ItemsSource = Province is { } province
            ? ThaiAreas.DistrictsOf(province).Where(a => _narrowing is null || _narrowing.Districts.Contains(a)).ToArray() : [];
        SubdistrictInput.ItemsSource = null;
        if (_narrowing is not null && DistrictInput.Items.Count == 1)
        {
            DistrictInput.SelectedIndex = 0;
            SubdistrictInput.ItemsSource = _narrowing.Subdistricts.Where(a => a.Code.StartsWith(District!, StringComparison.Ordinal)).ToArray();
            if (SubdistrictInput.Items.Count == 1) SubdistrictInput.SelectedIndex = 0;
        }
        _busy = false;
        Suggest();
    }

    private void OnDistrictChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SubdistrictInput.ItemsSource = District is { } district
            ? ThaiAreas.SubdistrictsOf(district).Where(a => _narrowing is null || _narrowing.Subdistricts.Contains(a)).ToArray() : [];
        if (_narrowing is not null && SubdistrictInput.Items.Count == 1) SubdistrictInput.SelectedIndex = 0;
        _busy = false;
        Suggest();
    }

    private void OnAreaItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ComboBoxItem item &&
            ItemsControl.ItemsControlFromItemContainer(item) is ComboBox input)
            ConfirmAreaChoice(input, ThaiAddressChoice.ItemClick);
    }

    private void OnAreaKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var input = sender as ComboBox;
        if (sender is ComboBoxItem item)
            input = ItemsControl.ItemsControlFromItemContainer(item) as ComboBox;
        if (input is { IsDropDownOpen: true } &&
            (sender is ComboBoxItem { Content: ThaiArea } || input.SelectedItem is ThaiArea))
            ConfirmAreaChoice(input, ThaiAddressChoice.ItemEnter);
    }

    private void ConfirmAreaChoice(ComboBox input, ThaiAddressChoice choice)
    {
        if (_busy) return;
        var field = input == ProvinceInput ? ThaiAddressField.Province
            : input == DistrictInput ? ThaiAddressField.District : ThaiAddressField.Subdistrict;
        _autoSelectedAreas = ThaiAddressAssist.ConfirmChoice(_autoSelectedAreas, field, choice);
    }

    private void OnSubdistrictChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_busy)
        {
            Suggest();
        }
    }

    private void Suggest()
    {
        _busy = true;
        if (Subdistrict is null && _lastAutoFilled is not null && PostcodeInput.Text == _lastAutoFilled)
        {
            PostcodeInput.Text = string.Empty;
            _lastAutoFilled = null;
        }
        var suggestion = ThaiAddressAssist.SuggestPostcode(Subdistrict, PostcodeInput.Text, _lastAutoFilled);
        RefreshOptions();
        if (suggestion.TextToSet is { } text)
        {
            PostcodeInput.Text = text;
            _lastAutoFilled = text;
        }
        _notice = suggestion.Notice;
        _busy = false;
        CheckNotice();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPostcodeChanged(object sender, TextChangedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        _lastAutoFilled = null;
        var change = ThaiAddressAssist.ChangePostcode(PostcodeInput.Text,
            new(Province, District, Subdistrict), _autoSelectedAreas);
        _autoSelectedAreas = change.AutoSelected;
        _narrowing = change.Narrowing.IsFiltered ? change.Narrowing : null;
        _notice = change.Narrowing.Notice;
        ProvinceInput.ItemsSource = _narrowing?.Provinces ?? ThaiAreas.Provinces;
        Select(ProvinceInput, change.Selection.Province);
        DistrictInput.ItemsSource = Province is { } province
            ? _narrowing?.Districts ?? ThaiAreas.DistrictsOf(province) : [];
        Select(DistrictInput, change.Selection.District);
        SubdistrictInput.ItemsSource = District is { } district
            ? _narrowing?.Subdistricts ?? ThaiAreas.SubdistrictsOf(district) : [];
        Select(SubdistrictInput, change.Selection.Subdistrict);
        RefreshOptions();
        _busy = false;
        CheckNotice();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CheckNotice()
    {
        _notice = ThaiAddressAssist.NoticeFor(new(Province, District, Subdistrict), Postcode);
        RefreshNotice();
    }

    private void RefreshOptions()
    {
        if (_optionsSubdistrict == Subdistrict && _optionsLanguage == Loc.Language) return;
        _optionsSubdistrict = Subdistrict;
        _optionsLanguage = Loc.Language;
        var text = PostcodeInput.Text;
        PostcodeInput.ItemsSource = ThaiPostcodes.PostcodesOf(Subdistrict)
            .Select(p => new PostcodeOption(p.Postcode, Loc.IsThai ? p.DetailTh : p.DetailEn)).ToArray();
        PostcodeInput.Text = text;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshText();

    private void RefreshText()
    {
        _busy = true;
        RefreshOptions();
        _busy = false;
        RefreshNotice();
    }

    private void RefreshNotice() => NoticeText.Text = _notice switch
    {
        ThaiAddressNotice.MultiplePostcodes => Loc.T("This subdistrict has several postcodes. Check which one fits the address."),
        ThaiAddressNotice.PostcodeMismatch => Loc.T("This postcode does not match the selected subdistrict in the app's data. You can still use it."),
        ThaiAddressNotice.PostcodeNotFound => Loc.T("This postcode or subdistrict is not in the app's postal data. You can still use this postcode."),
        _ => string.Empty,
    };

    private sealed record PostcodeOption(string Postcode, string Detail);
}
