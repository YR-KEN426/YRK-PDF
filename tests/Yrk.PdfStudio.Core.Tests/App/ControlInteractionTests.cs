using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.Views;
using Yrk.PdfStudio.Core.Pdf.ETax;
using Yrk.PdfStudio.Testing;
using Calendar = System.Windows.Controls.Calendar;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class ControlInteractionTests
{
    [Fact]
    public void Editable_combo_types_selects_and_keeps_native_keyboard_and_popup_behavior()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            var model = new ComboValue();
            var combo = new ComboBox { IsEditable = true, IsTextSearchEnabled = false, ItemsSource = new[] { "10220", "10550", "10230" } };
            combo.SetBinding(ComboBox.TextProperty, new Binding(nameof(ComboValue.Text)) { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            var host = Host(combo);
            try
            {
                var editor = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
                Type(editor, "99999");
                Assert.Equal("99999", combo.Text);
                Assert.Equal("99999", model.Text);

                combo.IsDropDownOpen = true;
                WpfTestApp.Pump();
                var popup = Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
                Assert.True(popup.IsOpen);
                var peer = new ComboBoxAutomationPeer(combo).GetChildren().Single(p => p.GetName() == "10550");
                ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)).Select();
                combo.IsDropDownOpen = false;
                WpfTestApp.Pump();
                Assert.False(popup.IsOpen);
                Assert.Equal("10550", combo.SelectedItem);
                Assert.Equal("10550", editor.Text);
                Assert.Equal("10550", model.Text);
                KeyDown(editor, Key.Down);
                Assert.Equal("10230", combo.SelectedItem);
                KeyDown(editor, Key.Up);
                Assert.Equal("10550", combo.SelectedItem);
                KeyDown(editor, Key.F4);
                Assert.True(popup.IsOpen);
                KeyDown(editor, Key.Escape);
                Assert.False(popup.IsOpen);
                combo.IsDropDownOpen = true;
                WpfTestApp.Pump();
                KeyDown(editor, Key.Enter);
                Assert.False(popup.IsOpen);
                KeyDown(editor, Key.Escape);
                Assert.False(popup.IsOpen);

                // A caller's explicit container style remains authoritative.
                var custom = new Style(typeof(ComboBoxItem), (Style)Application.Current.Resources[typeof(ComboBoxItem)]);
                custom.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(13)));
                combo.ItemContainerStyle = custom;
                combo.IsDropDownOpen = true;
                WpfTestApp.Pump();
                Assert.Equal(new Thickness(13), ((ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(0)).Padding);
                combo.IsDropDownOpen = false;
            }
            finally { host.Close(); WpfTestApp.Pump(); }
            Assert.Empty(app.Errors);
            return true;
        });
    }

    [Theory]
    [InlineData("en-US", "en", "11/18/2026", 2026)]
    [InlineData("th-TH", "th", "18/11/2026", 2026)]
    [InlineData("th-TH", "th", "18/11/2569", 2569)]
    public void Etax_date_editor_preserves_native_parsing_including_buddhist_input_and_keeps_watermark(string culture, string language, string input, int nativeYear)
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp(language);
            var previousCulture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var host = new ETaxInvoiceDialog(new ETaxSettings { Type = TaxDocumentType.CreditNote });
            host.Language = XmlLanguage.GetLanguage(culture);
            Show(host);
            try
            {
                var date = (DatePicker)host.FindName("ReferenceDateInput");
                date.Language = XmlLanguage.GetLanguage(culture);
                var editor = Assert.IsType<DatePickerTextBox>(date.Template.FindName("PART_TextBox", date));
                var watermark = Assert.IsAssignableFrom<ContentControl>(editor.Template.FindName("PART_Watermark", editor));
                Assert.Equal(Loc.T("Select a date"), watermark.Content);
                Type(editor, input);
                KeyDown(editor, Key.Enter);
                // WPF uses a Gregorian calendar even for th-TH. Step 3 preserves this existing
                // behavior; interpreting Buddhist years as Gregorian dates requires an e-Tax change.
                var baseline = new DatePicker { Style = null, Language = XmlLanguage.GetLanguage(culture), Text = input };
                Assert.Equal(new DateTime(nativeYear, 11, 18), baseline.SelectedDate);
                Assert.Equal(baseline.SelectedDate, date.SelectedDate);
                Assert.Equal(Visibility.Collapsed, watermark.Visibility);
                var build = typeof(ETaxInvoiceDialog).GetMethod("Build", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var invoice = (TaxInvoice)build.Invoke(host, null)!;
                Assert.Equal(date.SelectedDate, invoice.Reference!.IssueDate);

                date.IsDropDownOpen = true;
                WpfTestApp.Pump();
                var popup = Assert.IsType<Popup>(date.Template.FindName("PART_Popup", date));
                Assert.True(popup.IsOpen);
                var calendar = WpfTestApp.Descendants(popup.Child).OfType<Calendar>().Single();
                var calendarItem = Assert.IsType<CalendarItem>(calendar.Template.FindName("PART_CalendarItem", calendar));
                var header = Assert.IsType<Button>(calendarItem.Template.FindName("PART_HeaderButton", calendarItem));
                Invoke(header); WpfTestApp.Pump();
                Assert.Equal(CalendarMode.Year, calendar.DisplayMode);
                Assert.Equal(Visibility.Visible, ((Grid)calendarItem.Template.FindName("PART_YearView", calendarItem)).Visibility);
                Invoke(header); WpfTestApp.Pump();
                Assert.Equal(CalendarMode.Decade, calendar.DisplayMode);
                calendar.DisplayMode = CalendarMode.Month;
                WpfTestApp.Pump();
                var month = Assert.IsType<Grid>(calendarItem.Template.FindName("PART_MonthView", calendarItem));
                Assert.Equal(42, month.Children.OfType<CalendarDayButton>().Count());
                Assert.All(month.Children.OfType<CalendarDayButton>(), b => Assert.True(b.ActualWidth >= 28 && b.ActualHeight >= 26));
                var day = month.Children.OfType<CalendarDayButton>().Single(b => Equals(b.DataContext, new DateTime(nativeYear, 11, 20)));
                Invoke(day);
                WpfTestApp.Pump();
                Assert.Equal(new DateTime(nativeYear, 11, 20), date.SelectedDate);
                day.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                WpfTestApp.Pump();
                Assert.False(popup.IsOpen);
                date.SelectedDate = null;
                WpfTestApp.Pump();
                Assert.Equal(Visibility.Visible, watermark.Visibility);
                Assert.Equal(Loc.T("Select a date"), watermark.Content);
            }
            finally { host.Close(); WpfTestApp.Pump(); CultureInfo.CurrentCulture = previousCulture; }
            Assert.Empty(app.Errors);
            return true;
        });
    }

    [Theory]
    [InlineData("en")]
    [InlineData("th")]
    public void Sidebar_icons_fit_one_row_and_keep_headers_selection_and_accessible_labels(string language)
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp(language);
            using var fixture = new WindowFixture();
            var window = WindowCatalog.Cases.Single(c => c.Name == "MainWindow").Create!(fixture);
            try
            {
                var pageTab = Assert.IsType<TabItem>(LogicalTreeHelper.GetParent((ListBox)window.FindName("ThumbnailList")));
                var tabs = Assert.IsType<TabControl>(ItemsControl.ItemsControlFromItemContainer(pageTab));
                ((Border)tabs.Parent).Visibility = Visibility.Visible; // Inspect the 240px sidebar with an empty fixture.
                Show(window);
                var headers = new[] { "Pages", "Comments", "Signatures", "Layers", "Bookmarks", "Reading marks" };
                var items = tabs.Items.Cast<TabItem>().ToArray();
                Assert.Equal(headers.Length, items.Length);
                Assert.True(items.Sum(t => t.ActualWidth) <= tabs.ActualWidth + 1);
                for (var i = 0; i < items.Length; i++)
                {
                    var item = items[i];
                    Assert.Equal(Loc.T(headers[i]), item.Header);
                    Assert.Equal(item.Header, item.ToolTip);
                    Assert.Equal(item.Header, System.Windows.Automation.AutomationProperties.GetName(item));
                    Assert.True(item.ActualWidth >= 32);
                    Assert.Equal(items[0].TranslatePoint(new Point(), tabs).Y, item.TranslatePoint(new Point(), tabs).Y);
                    var icon = Assert.Single(WpfTestApp.Descendants(item).OfType<TextBlock>());
                    Assert.Equal(item.Tag, icon.Text);
                    item.IsSelected = true;
                    WpfTestApp.Layout(window);
                    Assert.Equal(i, tabs.SelectedIndex);
                    Assert.Equal(Visibility.Visible, ((Border)item.Template.FindName("Underline", item)).Visibility);
                }
                Assert.Same(items[1], window.FindName("CommentsTab"));
                Assert.Same(items[2], window.FindName("SignaturesTab"));
                Assert.Same(items[3], window.FindName("LayersTab"));
            }
            finally { window.Close(); WpfTestApp.Pump(); }
            Assert.True(app.Errors.Count == 0, string.Join(Environment.NewLine, app.Errors));
            return true;
        });
    }

    [Fact]
    public void Color_rings_preserve_stamp_ink_and_reader_mark_values_and_selection()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            var stamp = new StampDialog(new StampSettings());
            var reader = new ReaderBookmarkDialog("Example", "purple", existing: true);
            try
            {
                Show(stamp);
                var inkNames = new[] { "RedInk", "BlueInk", "GreenInk", "BlackInk" };
                var inks = new[] { StampInk.Red, StampInk.Blue, StampInk.Green, StampInk.Black };
                var colors = new[] { "#C62828", "#1D3F9A", "#1B7A3A", "#202020" };
                for (var i = 0; i < inkNames.Length; i++)
                {
                    var ring = (RadioButton)stamp.FindName(inkNames[i]);
                    ring.IsChecked = true;
                    WpfTestApp.Pump();
                    var design = (StampDesign)typeof(StampDialog).GetProperty("Current", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(stamp)!;
                    Assert.Equal(inks[i], design.Ink);
                    Assert.Equal((Color)ColorConverter.ConvertFromString(colors[i]), ((SolidColorBrush)ring.Background).Color);
                    Assert.Same(Application.Current.Resources["ColorRing"], ring.Style);
                }
                Show(reader);
                var selector = (ListBox)reader.FindName("ColourInput");
                var rings = WpfTestApp.Descendants(selector).OfType<RadioButton>().ToArray();
                Assert.Equal(6, rings.Length);
                Assert.True(rings[5].IsChecked);
                var values = new[] { "red", "orange", "yellow", "green", "blue", "purple" };
                for (var i = 0; i < rings.Length; i++)
                {
                    ((ISelectionItemProvider)new RadioButtonAutomationPeer(rings[i]).GetPattern(PatternInterface.SelectionItem)).Select();
                    WpfTestApp.Pump();
                    Assert.Equal(values[i], selector.SelectedValue);
                    Assert.Single(rings, r => r.IsChecked == true);
                    Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(rings[i])));
                }
                selector.SelectedValue = "red";
                WpfTestApp.Pump();
                Assert.True(rings[0].IsChecked);
            }
            finally { stamp.Close(); reader.Close(); WpfTestApp.Pump(); }
            Assert.True(app.Errors.Count == 0, string.Join(Environment.NewLine, app.Errors));
            return true;
        });
    }

    [Fact]
    public void Keyboard_focus_displays_a_frame_on_each_control()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            var combo = new ComboBox();
            var date = new DatePicker();
            var tab = new TabItem { Header = "Draw" };
            var ring = new RadioButton { Style = (Style)Application.Current.Resources["ColorRing"] };
            var host = Host(new StackPanel { Children = { combo, date, new TabControl { Items = { tab } }, ring } });
            try
            {
                foreach (var control in new Control[] { combo, date, tab, ring })
                {
                    var target = control == date ? (Control)date.Template.FindName("PART_TextBox", date) : control;
                    Keyboard.Focus(target);
                    WpfTestApp.Pump();
                    Assert.True(control.IsKeyboardFocusWithin);
                    var frame = Assert.IsAssignableFrom<FrameworkElement>(control.Template.FindName("Focus", control));
                    Assert.Equal(Visibility.Visible, frame.Visibility);
                }
            }
            finally { host.Close(); WpfTestApp.Pump(); }
            Assert.Empty(app.Errors);
            return true;
        });
    }

    [Fact]
    public void Postcode_editor_still_notifies_address_fields_and_updates_the_province()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            var fields = new ThaiAddressFields();
            var changes = 0;
            fields.Changed += (_, _) => changes++;
            var host = Host(fields);
            try
            {
                var postcode = (ComboBox)fields.FindName("PostcodeInput");
                var editor = Assert.IsType<TextBox>(postcode.Template.FindName("PART_EditableTextBox", postcode));
                Type(editor, "10550");
                Assert.Equal("10550", fields.Postcode);
                Assert.Equal("11", fields.Province);
                Assert.Equal("110205", fields.Subdistrict);
                Type(editor, "10220");
                Assert.Equal("10", fields.Province);
                var province = (ComboBox)fields.FindName("ProvinceInput");
                Assert.Equal(ThaiAddressAssist.Narrow("10220").Provinces, province.Items.OfType<ThaiArea>());
                // Selecting an area still fills suggestions and uses the editable selection text path.
                ((ComboBox)fields.FindName("DistrictInput")).SelectedItem = ThaiAreas.DistrictsOf("10").Single(a => a.Code == "1005");
                ((ComboBox)fields.FindName("SubdistrictInput")).SelectedItem = ThaiAreas.SubdistrictsOf("1005").Single(a => a.Code == "100508");
                postcode.IsDropDownOpen = true;
                WpfTestApp.Pump();
                Assert.Equal(2, postcode.Items.Count);
                postcode.SelectedIndex = 1;
                postcode.IsDropDownOpen = false;
                WpfTestApp.Pump();
                Assert.Equal("10230", editor.Text);
                Assert.Equal("10230", fields.Postcode);
                Assert.True(changes >= 3);
            }
            finally { host.Close(); WpfTestApp.Pump(); }
            Assert.Empty(app.Errors);
            return true;
        });
    }

    private static void Type(TextBox editor, string value)
    {
        editor.Focus(); editor.SelectAll(); editor.SelectedText = value;
        WpfTestApp.Pump();
    }

    private static void KeyDown(UIElement target, Key key, bool system = false)
    {
        var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        if (system)
            typeof(KeyEventArgs).GetMethod("MarkSystem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(input, null);
        target.RaiseEvent(input);
        if (!input.Handled)
        {
            input.RoutedEvent = Keyboard.KeyDownEvent;
            target.RaiseEvent(input);
        }
        WpfTestApp.Pump();
    }

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static Window Host(UIElement content)
    {
        var host = new Window { Content = content, Width = 480, Height = 620 };
        Show(host);
        return host;
    }

    private static void Show(Window window)
    {
        window.ShowActivated = false; window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -32000;
        window.Show(); WpfTestApp.Layout(window);
    }

    public sealed class ComboValue { public string Text { get; set; } = ""; }
}
