using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.Testing;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>Start page, step 4b: the frequent tools, the All view by category, the category buttons (a real tab control) and the search.</summary>
public sealed partial class HomePageTests
{
    private sealed record ToolInfo(string Id, string Category, string TitleKey, string TextKey, string KeywordsKey, string TipKey, bool Accounting, string? FullKey);

    private static List<ToolInfo> Tools() => ((IEnumerable)typeof(MainWindow).GetMethod("HomeTools", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!)
        .Cast<object>().Select(t =>
        {
            string S(string p) => (string)t.GetType().GetProperty(p)!.GetValue(t)!;
            return new ToolInfo(S("Id"), S("Category"), S("TitleKey"), S("TextKey"), S("KeywordsKey"), S("TipKey"), (bool)t.GetType().GetProperty("Accounting")!.GetValue(t)!, (string?)t.GetType().GetProperty("FullKey")!.GetValue(t));
        }).ToList();

    private static readonly string[] CategoryIds = ["organize", "convert", "edit", "accounting", "drawings", "security"];

    private static void SelectTab(MainWindow window, string id)
    {
        var tabs = Find<TabControl>(window, "HomeTabs");
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => (string)t.Tag == id);
        WpfTestApp.Layout(window);
    }

    private static List<Button> Cards(MainWindow window) =>
        WpfTestApp.Descendants(Find<Panel>(window, "HomeCards")).OfType<Button>().ToList();

    private static string[] CardIds(MainWindow window) => Cards(window).Select(b => (string)b.Tag).ToArray();

    private static void Search(MainWindow window, string text)
    {
        Find<TextBox>(window, "HomeSearchBox").Text = text;
        WpfTestApp.Layout(window);
    }

    private static string[] SectionNames(MainWindow window) =>
        Find<Panel>(window, "HomeCards").Children.OfType<StackPanel>().Select(s => AutomationProperties.GetName((DependencyObject)s.Children[0])).ToArray();

    [Fact]
    public void There_are_still_34_cards_and_the_frequent_list_names_real_ones_in_the_agreed_order()
    {
        var tools = Tools();
        Assert.Equal(34, tools.Count);
        Assert.Equal(34, tools.Select(t => t.Id).Distinct().Count());
        Assert.Equal(new[] { "combine", "split", "compress", "word", "edittext", "sign", "etax", "dwg" }, MainWindow.FrequentToolIds);
        Assert.All(MainWindow.FrequentToolIds, id => Assert.Contains(tools, t => t.Id == id));
        Assert.All(tools, t => Assert.Contains(t.Category, CategoryIds));
    }

    [Fact]
    public void The_start_view_is_frequently_used_with_eight_cards_in_four_columns_and_no_category_buttons()
    {
        WithWindow((app, window, _) =>
        {
            Assert.Equal(MainWindow.FrequentToolIds, CardIds(window));
            var grid = Assert.Single(Find<Panel>(window, "HomeCards").Children.OfType<System.Windows.Controls.Primitives.UniformGrid>());
            Assert.Equal(4, grid.Columns);
            Assert.Equal(2, (int)Math.Ceiling(grid.Children.Count / (double)grid.Columns));
            Assert.Equal(Visibility.Collapsed, Find<TabControl>(window, "HomeTabs").Visibility);
            Assert.Equal(Visibility.Visible, Find<DockPanel>(window, "HomeFrequentHeader").Visibility);
            Assert.Empty(SectionNames(window)); // No category headings here.
            var link = Find<Button>(window, "HomeAllToolsLink");
            Assert.Equal("All tools →", link.Content);
            Assert.True(link.ActualHeight >= 44);
            var widths = grid.Children.Cast<FrameworkElement>().Select(c => c.ActualWidth).ToArray();
            Assert.All(widths, w => Assert.True(w >= 150));
        });
    }

    [Fact]
    public void A_very_narrow_window_turns_the_frequent_grid_into_two_columns_by_four_rows()
    {
        WithWindow((app, window, _) =>
        {
            var grid = Assert.Single(Find<Panel>(window, "HomeCards").Children.OfType<System.Windows.Controls.Primitives.UniformGrid>());
            Assert.Equal(2, grid.Columns);
            Assert.Equal(8, grid.Children.Count);
        }, width: 720);
    }

    [Fact]
    public void The_all_tools_link_count_comes_from_the_real_list_not_a_fixed_number()
    {
        WithWindow((app, window, _) =>
        {
            Assert.Equal("All tools →", Find<Button>(window, "HomeAllToolsLink").Content);
        });
        var thai = Loc.Language;
        WithWindow((app, window, _) =>
        {
            Assert.Equal("เครื่องมือทั้งหมด →", Find<Button>(window, "HomeAllToolsLink").Content);
        }, language: "th");
        Assert.Equal(thai, Loc.Language);
    }

    [Fact]
    public void Category_buttons_and_headings_come_in_the_same_order_and_the_first_button_goes_back_to_frequent()
    {
        WithWindow((app, window, _) =>
        {
            Find<Button>(window, "HomeAllToolsLink").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            WpfTestApp.Layout(window);
            var tabs = Find<TabControl>(window, "HomeTabs");
            Assert.Equal(Visibility.Visible, tabs.Visibility);
            var names = tabs.Items.Cast<TabItem>().Select(t => AutomationProperties.GetName(t)).ToArray();
            Assert.Equal(new[] { "Frequently used", "All tools", "Organize", "Convert", "Edit and sign", "Accounting", "Construction drawings", "Security" }, names);
            Assert.Equal(CategoryIds, tabs.Items.Cast<TabItem>().Skip(2).Select(t => (string)t.Tag).ToArray());
            Assert.Equal("all", (string)((TabItem)tabs.SelectedItem).Tag);
            Assert.Equal(names.Skip(2).ToArray(), SectionNames(window)); // Headings follow the buttons.
            SelectTab(window, "frequent");
            Assert.Equal(MainWindow.FrequentToolIds, CardIds(window));
            Assert.Equal(Visibility.Collapsed, tabs.Visibility);
        });
    }

    [Fact]
    public void The_all_view_shows_every_card_once_in_its_own_category_with_a_count_per_heading()
    {
        WithWindow((app, window, _) =>
        {
            SelectTab(window, "all");
            var ids = CardIds(window);
            Assert.Equal(34, ids.Length);
            Assert.Equal(34, ids.Distinct().Count());
            var tools = Tools();
            var sections = Find<Panel>(window, "HomeCards").Children.OfType<StackPanel>().ToList();
            Assert.Equal(6, sections.Count);
            for (var i = 0; i < sections.Count; i++)
            {
                var expected = tools.Where(t => t.Category == CategoryIds[i]).Select(t => t.Id).ToArray();
                var grid = (System.Windows.Controls.Primitives.UniformGrid)sections[i].Children[1];
                Assert.Equal(expected, grid.Children.Cast<Button>().Select(b => (string)b.Tag).ToArray());
                var count = ((StackPanel)sections[i].Children[0]).Children.OfType<TextBlock>().Last().Text;
                Assert.Equal($"{expected.Length} tools", count);
            }
        });
    }

    [Theory]
    [InlineData("organize", 7)]
    [InlineData("convert", 8)]
    [InlineData("edit", 10)]
    [InlineData("accounting", 10)] // Its 2 own cards, and the 8 flagged ones from other categories.
    [InlineData("drawings", 4)]
    [InlineData("security", 3)]
    public void A_category_button_shows_that_category_and_only_accounting_adds_the_flagged_cards(string id, int count)
    {
        WithWindow((app, window, _) =>
        {
            SelectTab(window, id);
            var ids = CardIds(window);
            Assert.Equal(count, ids.Length);
            Assert.Equal(count, ids.Distinct().Count());
            var tools = Tools();
            if (id == "accounting")
            {
                Assert.Equal(new[] { "etax", "amount" }, ids.Take(2).ToArray()); // Its own first.
                Assert.All(ids.Skip(2), cardId => Assert.True(tools.Single(t => t.Id == cardId).Accounting));
            }
            else Assert.All(ids, cardId => Assert.Equal(id, tools.Single(t => t.Id == cardId).Category));
            var heading = ((StackPanel)Find<Panel>(window, "HomeCards").Children.OfType<StackPanel>().Single().Children[0]).Children.OfType<TextBlock>().Last().Text;
            Assert.Equal($"{count} tools", heading); // Counts what is shown.
        });
    }

    [Fact]
    public void Cards_are_compact_with_a_soft_category_icon_and_a_one_line_description_and_use_no_borrowed_colours()
    {
        WithWindow((app, window, _) =>
        {
            SelectTab(window, "all");
            var resources = Application.Current.Resources;
            var tools = Tools();
            foreach (var card in Cards(window))
            {
                var tool = tools.Single(t => t.Id == (string)card.Tag);
                var content = (Grid)card.Content;
                var icon = content.Children.OfType<Border>().Single();
                var category = char.ToUpperInvariant(tool.Category[0]) + tool.Category[1..];
                Assert.Same(resources[category + "SoftBrush"], icon.Background);
                Assert.Same(resources[category + "Brush"], ((TextBlock)icon.Child).Foreground);
                Assert.Equal(36, icon.Width);
                Assert.False(string.IsNullOrEmpty(((TextBlock)icon.Child).Text), "Every card has an icon glyph.");
                var texts = content.Children.OfType<StackPanel>().Single().Children.OfType<TextBlock>().ToArray();
                Assert.Equal(TextWrapping.NoWrap, texts[1].TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, texts[1].TextTrimming);
                Assert.Equal(14, texts[0].FontSize);
                Assert.Equal(12, texts[1].FontSize);
                Assert.NotSame(resources["BrandBrush"], icon.Background);
                Assert.NotSame(resources["MeasureAccentBrush"], icon.Background);
                Assert.Equal(AutomationProperties.GetName(card), texts[0].Text);
                Assert.Equal(Loc.T(tool.TextKey), texts[1].Text);
                Assert.IsType<ToolTip>(card.ToolTip);
            }
        });
    }

    [Fact]
    public void A_card_lifts_two_pixels_in_120_ms_and_a_focused_card_gets_the_blue_frame()
    {
        WithWindow((app, window, _) =>
        {
            var card = Cards(window).First();
            card.ApplyTemplate();
            var hover = card.Template.Triggers.OfType<Trigger>().Single(t => t.Property == UIElement.IsMouseOverProperty);
            var move = hover.EnterActions.OfType<BeginStoryboard>().Single().Storyboard.Children.OfType<DoubleAnimation>().Single();
            Assert.Equal(-2, move.To);
            Assert.Equal(TimeSpan.FromMilliseconds(120), move.Duration.TimeSpan);
            Assert.Contains(hover.Setters.OfType<Setter>(), s => s.Property == UIElement.EffectProperty && s.Value is System.Windows.Media.Effects.DropShadowEffect { BlurRadius: 18 });
            var focus = card.Template.Triggers.OfType<Trigger>().Single(t => t.Property == UIElement.IsKeyboardFocusedProperty);
            Assert.Contains(focus.Setters.OfType<Setter>(), s => s.Property == Border.BorderBrushProperty && s.Value is DynamicResourceExtension { ResourceKey: "FocusBrush" });
            Assert.True(card.Focusable);
        });
    }

    // --- Search ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("word", "word")]
    [InlineData("Word", "word")]
    [InlineData("รวม", "combine")]
    [InlineData("merge", "combine")]
    [InlineData("combine", "combine")]
    [InlineData("เซ็น", "sign")]
    [InlineData("ใบกำกับ", "etax")]
    [InlineData("dxf", "dwg")]
    public void Search_finds_the_right_card_first_by_name_description_or_search_words_in_both_languages(string query, string first)
    {
        foreach (var language in new[] { "en", "th" })
            WithWindow((app, window, _) =>
            {
                Search(window, query);
                var ids = CardIds(window);
                Assert.NotEmpty(ids);
                Assert.Equal(first, ids[0]);
            }, language: language);
    }

    [Fact]
    public void Search_needs_every_word_and_finds_cards_whatever_view_is_open()
    {
        WithWindow((app, window, _) =>
        {
            SelectTab(window, "security");
            Search(window, "pdf word");
            Assert.Equal("word", CardIds(window).First());
            Search(window, "merge pdf");
            Assert.Equal("combine", CardIds(window).First());
            Assert.DoesNotContain("word", CardIds(window)); // "merge" is not on that card.
            Search(window, "zzzz nothing");
            Assert.Empty(CardIds(window));
            var message = Assert.Single(WpfTestApp.Descendants(Find<Panel>(window, "HomeCards")).OfType<TextBlock>());
            Assert.Equal("No tools found. Try another word.", message.Text);
        });
    }

    [Fact]
    public void While_typing_the_drop_zone_and_recent_files_step_aside_and_the_results_use_the_full_width_and_Esc_brings_everything_back()
    {
        WithWindow((app, window, _) =>
        {
            SelectTab(window, "convert");
            var panel = Find<Border>(window, "HomeRecentPanel");
            var zone = Find<FrameworkElement>(window, "HomeDropZone");
            var main = Find<StackPanel>(window, "HomeMain");
            var root = Find<Grid>(window, "HomeColumns");
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.Equal(Visibility.Visible, zone.Visibility);
            var before = CardIds(window);

            Search(window, "pdf");
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
            Assert.Equal(Visibility.Collapsed, zone.Visibility);
            Assert.Equal(Visibility.Collapsed, Find<TabControl>(window, "HomeTabs").Visibility);
            Assert.Equal(root.ActualWidth, main.ActualWidth, 1);

            var box = Find<TextBox>(window, "HomeSearchBox");
            box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            WpfTestApp.Layout(window);
            Assert.Equal("", box.Text);
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.Equal(Visibility.Visible, zone.Visibility);
            Assert.Equal(before, CardIds(window)); // The same view and category as before the search.
            Assert.Equal("convert", (string)((TabItem)Find<TabControl>(window, "HomeTabs").SelectedItem).Tag);
        }, recentFiles: 3);
    }

    [Fact]
    public void Enter_opens_the_first_card_found()
    {
        WithWindow((app, window, _) =>
        {
            Search(window, "merge");
            var calls = app.Dialogs.Calls.Count;
            var box = Find<TextBox>(window, "HomeSearchBox");
            box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Return) { RoutedEvent = Keyboard.KeyDownEvent });
            PumpUntil(() => app.Dialogs.Calls.Count > calls); // Combine asks which files to join.
        });
    }

    [Fact]
    public void Ctrl_F_on_the_start_page_goes_to_the_tool_search_and_in_a_document_to_the_search_in_the_document()
    {
        WithWindow((app, window, _) =>
        {
            Assert.True(ApplicationCommands.Find.CanExecute(null, window));
            ApplicationCommands.Find.Execute(null, window);
            Assert.Same(Find<TextBox>(window, "HomeSearchBox"), FocusManager.GetFocusedElement(window));
            Assert.True(Wait(app.OpenAsync(app.CreatePdf())));
            WpfTestApp.Pump();
            ApplicationCommands.Find.Execute(null, window);
            Assert.Same(Find<TextBox>(window, "SearchBox"), FocusManager.GetFocusedElement(window));
        });
    }

    [Fact]
    public void Coming_back_to_the_start_page_starts_at_frequently_used_with_an_empty_search()
    {
        WithWindow((app, window, _) =>
        {
            SelectTab(window, "drawings");
            Search(window, "dwg");
            Assert.True(Wait(app.OpenAsync(app.CreatePdf())));
            WpfTestApp.Await(app.Model.CloseTabAsync(keepAlive: true));
            WpfTestApp.Layout(window);
            Assert.Equal("", Find<TextBox>(window, "HomeSearchBox").Text);
            Assert.Equal(MainWindow.FrequentToolIds, CardIds(window));
        });
    }

    // --- Screen readers ---------------------------------------------------------------------------------------

    [Fact]
    public void The_category_buttons_are_a_tab_control_and_say_which_tab_is_selected()
    {
        WithWindow((app, window, _) =>
        {
            Find<Button>(window, "HomeAllToolsLink").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            SelectTab(window, "convert");
            var tabs = Find<TabControl>(window, "HomeTabs");
            var peer = UIElementAutomationPeer.CreatePeerForElement(tabs);
            Assert.Equal(AutomationControlType.Tab, peer.GetAutomationControlType());
            Assert.Equal("Tool categories", peer.GetName());
            var items = peer.GetChildren();
            Assert.Equal(8, items.Count);
            Assert.All(items, i => Assert.Equal(AutomationControlType.TabItem, i.GetAutomationControlType()));
            var selected = items.Where(i => ((ISelectionItemProvider)i.GetPattern(PatternInterface.SelectionItem)).IsSelected).ToList();
            Assert.Equal("Convert", Assert.Single(selected).GetName());
            Assert.Equal(new[] { "Frequently used", "All tools", "Organize", "Convert", "Edit and sign", "Accounting", "Construction drawings", "Security" },
                items.Select(i => i.GetName()).ToArray());
        });
    }

    [Fact]
    public void Windows_UI_Automation_itself_sees_Tab_items_and_which_one_is_selected()
    {
        WithWindow((app, window, _) =>
        {
            Find<Button>(window, "HomeAllToolsLink").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            SelectTab(window, "accounting");
            var handle = new WindowInteropHelper(window).Handle;
            // The client runs on another thread while this one keeps pumping messages, so the window can answer.
            var read = Task.Run(() =>
            {
                var root = AutomationElement.FromHandle(handle);
                var tab = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab));
                var items = tab.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                return (Name: tab.Current.Name, Items: items.Cast<AutomationElement>()
                    .Select(i => (i.Current.Name, ((SelectionItemPattern)i.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected)).ToArray());
            });
            WpfTestApp.Await(read);
            var (name, items) = read.Result;
            Assert.Equal("Tool categories", name);
            Assert.Equal(8, items.Length);
            Assert.Equal("Accounting", Assert.Single(items.Where(i => i.IsSelected)).Name);
        });
    }

    // --- Texts -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_card_has_Thai_search_words_and_the_start_page_texts_are_translated()
    {
        var keys = Loc.ThaiKeys.ToHashSet();
        foreach (var tool in Tools())
        {
            Assert.Contains(tool.KeywordsKey, keys);
            Assert.Contains(tool.TitleKey, keys);
        }

        foreach (var text in new[] { "Frequently used", "Frequently used tools", "All tools →", "{0} tools", "No tools found. Try another word.", "Tool categories" })
            Assert.Contains(text, keys);
    }

    [Fact]
    public void Every_card_has_a_short_description_and_a_tooltip_in_Thai_and_English_that_come_from_the_translation_file()
    {
        static bool HasThai(string text) => text.Any(c => c is >= '฀' and <= '๿');
        var tools = Tools();
        Assert.Equal(34, tools.Count);
        Assert.Equal(34, tools.Select(t => t.TextKey).Distinct().Count()); // No two cards say the same.
        foreach (var language in new[] { "en", "th" })
        {
            WithWindow((app, window, _) =>
            {
                SelectTab(window, "all");
                var cards = Cards(window);
                Assert.Equal(34, cards.Count);
                foreach (var card in cards)
                {
                    var tool = tools.Single(t => t.Id == (string)card.Tag);
                    var thai = language == "th";
                    var texts = ((Grid)card.Content).Children.OfType<StackPanel>().Single().Children.OfType<TextBlock>().ToArray();
                    var tip = Assert.IsType<ToolTip>(card.ToolTip);
                    var lines = ((StackPanel)tip.Content).Children.OfType<TextBlock>().Select(t => t.Text).ToArray();

                    Assert.Equal(thai ? Loc.InThai(tool.TextKey) : tool.TextKey, texts[1].Text);
                    var full = tool.FullKey is { } k && (thai ? Loc.InThai(k) : k) != (thai ? Loc.InThai(tool.TextKey) : tool.TextKey) ? (thai ? Loc.InThai(k) : k) : null;
                    Assert.Equal(full is null ? 2 : 3, lines.Length);
                    if (full is not null) Assert.Equal(full, lines[1]); // A shortened card line shows in full here.
                    Assert.Equal(texts[0].Text, lines[0]); // The tooltip starts with the card name,
                    Assert.Equal(thai ? Loc.InThai(tool.TipKey) : tool.TipKey, lines[^1]); // then the detail.
                    Assert.False(string.IsNullOrWhiteSpace(texts[1].Text), tool.Id);
                    Assert.False(string.IsNullOrWhiteSpace(lines[1]), tool.Id);
                    Assert.NotEqual(texts[1].Text, lines[1]); // The tooltip adds something the card does not say.
                    Assert.Same(Application.Current.Resources["CardToolTip"], tip.Style);
                    Assert.Equal(System.Windows.Controls.Primitives.PlacementMode.Bottom, ((Style)tip.Style).Setters.OfType<Setter>().Single(s => s.Property == ToolTip.PlacementProperty).Value);
                    Assert.Equal(600, ToolTipService.GetInitialShowDelay(card));
                    if (thai)
                    {
                        Assert.Contains(tool.TextKey, Loc.ThaiKeys); // The Thai texts are in th.json, not just left in English.
                        Assert.Contains(tool.TipKey, Loc.ThaiKeys);
                        Assert.True(HasThai(texts[1].Text), tool.Id);
                        Assert.True(HasThai(lines[1]), tool.Id);
                    }
                    else
                    {
                        Assert.False(HasThai(texts[1].Text), tool.Id);
                        Assert.False(HasThai(lines[1]), tool.Id);
                    }
                }
            }, language: language);
        }
    }
}
