using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;

namespace Yrk.PdfStudio.App;

/// <summary>
/// The start page: a card for each thing people come to do. A card for a tool that works on a document opens one
/// first (when the tab has none) and then starts the tool, so "PDF to Word" is one click and a file choice.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// One card. The texts are English keys (shown through <see cref="Loc"/>), so a search can look in both languages.
    /// </summary>
    /// <param name="NeedsDocument">False for tools that pick their own files (combine, images to PDF, many files).</param>
    /// <param name="TipKey">The detail lines of the tooltip, after the name.</param>
    /// <param name="Accounting">Shown in the Accounting group too, besides its own group.</param>
    private sealed record HomeTool(string Id, string Category, string Glyph, string TitleKey, string TextKey, string KeywordsKey, string TipKey,
        bool NeedsDocument = true, bool Accounting = false)
    {
        public string Title => Loc.T(TitleKey);

        public string Text => Loc.T(TextKey);

        public string Tip => Loc.T(TipKey);
    }

    /// <summary>Marks an English text as one the page translates (the translation check reads these).</summary>
    private static string K(string english) => english;

    /// <summary>The categories in the order of the buttons and the headings (brief 4.1), with their colours (Colors.xaml).</summary>
    private static readonly (string Id, string NameKey, string Brush, string SoftBrush)[] HomeCategories =
    [
        ("organize", K("Organize"), "OrganizeBrush", "OrganizeSoftBrush"),
        ("convert", K("Convert"), "ConvertBrush", "ConvertSoftBrush"),
        ("edit", K("Edit and sign"), "EditBrush", "EditSoftBrush"),
        ("accounting", K("Accounting"), "AccountingBrush", "AccountingSoftBrush"),
        ("drawings", K("Construction drawings"), "DrawingsBrush", "DrawingsSoftBrush"),
        ("security", K("Security"), "SecurityBrush", "SecuritySoftBrush"),
    ];

    /// <summary>The cards of the "Frequently used" view, in this order. Fixed in one place; nothing is counted.</summary>
    internal static readonly string[] FrequentToolIds = ["combine", "split", "compress", "word", "edittext", "sign", "etax", "dwg"];

    private const string FrequentTab = "frequent";
    private const string AllTab = "all";

    /// <summary>The buttons in order: Frequently used, All, then the six categories.</summary>
    internal static readonly string[] HomeTabIds = [FrequentTab, AllTab, .. HomeCategories.Select(c => c.Id)];

    private string _homeTab = FrequentTab;
    private string _homeQuery = string.Empty;
    private bool _buildingTabs;
    private readonly List<UniformGrid> _homeGrids = [];

    private HomeRecentList _homeRecent = null!;

    /// <summary>The cards, in the language shown (made again when it changes).</summary>
    private static List<HomeTool> HomeTools() =>
    [
        new("combine", "organize", "",
            K("Combine PDF"), K("Several files, one PDF"), K("merge, join, combine, put files together"), K("Drag files to set their order before combining."), NeedsDocument: false),
        new("split", "organize", "",
            K("Split PDF"), K("Save selected pages separately"), K("split, separate, cut, extract pages"), K("Enter page ranges or split every N pages.")),
        new("pages", "organize", "",
            K("Organize pages"), K("Manage PDF pages"), K("pages, rotate, delete pages, reorder, move pages"), K("Rotate, delete, move, insert, and extract pages.")),
        new("scanpdf", "organize", "",
            K("Scan to PDF"), K("Scan paper into PDF"), K("scan, scanner, paper to PDF"), K("Scan more pages and optionally recognize text."), NeedsDocument: false, Accounting: true),
        new("scan", "organize", "\uE7A8",
            K("Clean up scans"), K("Adjust scanned pages"), K("deskew, straighten, crop, blank pages, dark edges"), K("Straighten, trim dark edges, and remove blank pages."), Accounting: true),
        new("images", "organize", "",
            K("Images to PDF"), K("Make a PDF from images"), K("images, photos, JPG, PNG, picture to PDF"), K("Accepts JPG and PNG images."), NeedsDocument: false),
        new("batch", "organize", "",
            K("Process many files"), K("Process a whole folder"), K("batch, many files, folder, bulk"), K("Compress, convert, or mark many files."), NeedsDocument: false, Accounting: true),

        new("word", "convert", "",
            K("PDF to Word"), K("Editable text"), K("Word, docx, convert to Word, editable"), K("Tables and pictures stay in place.")),
        new("excel", "convert", "",
            K("PDF to Excel"), K("Tables become cells"), K("Excel, xlsx, spreadsheet, table, numbers"), K("Amounts are exported as numbers you can add up."), Accounting: true),
        new("compress", "convert", "",
            K("Compress PDF"), K("Make the file smaller"), K("compress, smaller, reduce size, shrink, email"), K("Choose from three levels and save a new file.")),
        new("powerpoint", "convert", "",
            K("PDF to PowerPoint"), K("One slide per page"), K("PowerPoint, pptx, slides, presentation"), K("Slides look like the PDF pages.")),
        new("pictures", "convert", "",
            K("PDF to images"), K("Export each page as an image"), K("PNG, JPG, page to picture, save as pictures"), K("Choose PNG or JPG.")),
        new("ocr", "convert", "",
            K("Recognize text (OCR)"), K("Search and copy scanned text"), K("OCR, text recognition, searchable, read scanned text"), K("Thai is supported; check recognized text before use."), Accounting: true),
        new("convertscan", "convert", "",
            K("Convert scanned documents"), K("Convert scans for reuse"), K("scanned documents, searchable PDF, scans to Word, scans to text"), K("Choose searchable PDF, Word, or text output."), NeedsDocument: false),
        new("pdfa", "convert", "",
            K("PDF/A for archiving"), K("Save as PDF/A"), K("PDF/A, archive, archiving, long-term"), K("A long-term archive format requested by many offices.")),

        new("edittext", "edit", "",
            K("Edit text"), K("Change words in a PDF"), K("edit text, change text, correct text, typo"), K("Edits one line at a time; paragraphs do not reflow.")),
        new("addtext", "edit", "",
            K("Add text"), K("Type on a page"), K("add text, type, write on PDF, text box"), K("Choose the font and size.")),
        new("addpicture", "edit", "",
            K("Add a picture"), K("Place and resize a picture"), K("add picture, insert image, logo, photo"), K("Move, turn and fade it; still editable after saving.")),
        new("fill", "edit", "",
            K("Fill in a form"), K("Fill or create form fields"), K("fill form, form, fields, create form, checkbox"), K("Works with text boxes, check boxes, radio buttons and drop-downs.")),
        new("sign", "edit", "",
            K("Sign"), K("Place a handwritten signature"), K("sign, signature, handwritten, autograph"), K("Draw, type, or use an image of your signature.")),
        new("digital", "edit", "",
            K("Digital signature"), K("Sign with a Digital ID"), K("digital signature, certificate, digital ID"), K("Later changes are shown."), Accounting: true),
        new("certified", "edit", "",
            K("Certified true copy"), K("Stamp a copy with your signature"), K("certified copy, true copy, certify"), K("Add a \"for … only\" line and the date."), Accounting: true),
        new("stamp", "edit", "\uE930",
            K("Rubber stamp"), K("Choose or edit a stamp"), K("rubber stamp, approved, received, paid, company stamp"), K("Change words, colour, and date, or save a company stamp for reuse."), Accounting: true),
        new("comment", "edit", "",
            K("Highlight and comment"), K("Mark and annotate"), K("highlight, comment, note, underline, annotate, draw"), K("Includes highlighting, underlining, notes, and drawing.")),
        new("marks", "edit", "",
            K("Watermark and page numbers"), K("Add marks to pages"), K("watermark, page numbers, header, footer"), K("Add a watermark, header, footer, or page numbers.")),

        new("etax", "accounting", "",
            K("e-Tax Invoice"), K("Create tax documents as PDF/A-3"), K("e-Tax, tax invoice, invoice, credit note, debit note, Revenue Department"), K("Supports tax invoices, debit and credit notes with Revenue Department XML."), NeedsDocument: false),
        new("amount", "accounting", "",
            K("Amount in words"), K("Convert amounts to words"), K("amount in words, baht text, number to text"), K("Produces Thai and English wording."), NeedsDocument: false),

        new("dwg", "drawings", "",
            K("Open DWG and DXF"), K("View drawings without AutoCAD"), K("DWG, DXF, AutoCAD, drawing, CAD"), K("Switch layers on and off and measure at the drawing's scale.")),
        new("measure", "drawings", "",
            K("Measure"), K("Measure at drawing scale"), K("measure, distance, area, length, perimeter, scale"), K("Snapping finds line ends and midpoints.")),
        new("count", "drawings", "",
            K("Count items"), K("Click to count items"), K("count, counting, quantity, takeoff, doors, lights"), K("Export the count list.")),
        new("compare", "drawings", "",
            K("Compare versions"), K("See differences between revisions"), K("compare, revisions, differences, versions, changes"), K("Compares saved files, so unsaved changes are excluded.")),

        new("protect", "security", "",
            K("Protect with password"), K("Set an open password or restrictions"), K("password, protect, encrypt, lock, permissions"), K("Choose restrictions such as printing, copying, or editing.")),
        new("unprotect", "security", "",
            K("Remove password"), K("Save an unlocked copy"), K("remove password, unlock, decrypt, open protected"), K("Files with restrictions require the owner password.")),
        new("redact", "security", "",
            K("Black out (redact)"), K("Permanently remove private details"), K("redact, black out, hide private, censor, sensitive"), K("Text, drawings and comments under the box are removed; can mark every match of a search.")),
    ];

    private void InitializeHome()
    {
        // The start page fills the document area, below the tab strip and the update bar when they are there.
        void KeepBelowBars() => HomePage.Margin = new Thickness(0,
            (TabStripBar.IsVisible ? TabStripBar.ActualHeight : 0) + (UpdateBanner.IsVisible ? UpdateBanner.ActualHeight : 0), 0, 0);
        foreach (var bar in new FrameworkElement[] { TabStripBar, UpdateBanner })
        {
            bar.SizeChanged += (_, _) => KeepBelowBars();
            bar.IsVisibleChanged += (_, _) => KeepBelowBars();
        }

        _homeRecent = new HomeRecentList(dispatcher: Dispatcher) { Enabled = _viewModel.ShowRecentOnHome };
        HomeRecentPanel.DataContext = _homeRecent;
        _homeRecent.PropertyChanged += (_, _) => UpdateHomeLayout();
        void OnShowRecentChanged(object? sender, EventArgs e) => _homeRecent.Enabled = _viewModel.ShowRecentOnHome;
        MainViewModel.ShowRecentOnHomeChanged += OnShowRecentChanged;
        Closed += (_, _) =>
        {
            MainViewModel.ShowRecentOnHomeChanged -= OnShowRecentChanged;
            _homeRecent.Dispose();
        };
        // Each time the page comes back: look at the files again (one deleted meanwhile leaves the list), and start from the
        // frequently used tools with an empty search.
        HomePage.IsVisibleChanged += (_, _) =>
        {
            if (!HomePage.IsVisible) return;
            _ = _homeRecent.RefreshAsync();
            _homeTab = FrequentTab;
            _homeQuery = string.Empty;
            HomeSearchBox.Clear();
            ShowHomeTabs();
        };
        HomePage.SizeChanged += (_, _) => UpdateHomeLayout();
        HomeMain.SizeChanged += (_, _) => UpdateCardColumns();

        ShowHomeTabs();
        ShowHomeLanguage();
        UpdateHomeLayout();
        _ = _homeRecent.RefreshAsync();
        Loc.LanguageChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            ShowHomeTabs();
            ShowHomeLanguage();
        });
    }

    // --- Tabs, cards and search -------------------------------------------------------------------------

    private static (string Id, string NameKey, string Brush, string SoftBrush) CategoryOf(string id) => HomeCategories.Single(c => c.Id == id);

    /// <summary>Makes the category buttons (in the language shown) and then the cards.</summary>
    private void ShowHomeTabs()
    {
        _buildingTabs = true;
        HomeTabs.Items.Clear();
        foreach (var id in HomeTabIds)
        {
            var name = Loc.T(id switch { FrequentTab => "Frequently used", AllTab => "All tools", _ => CategoryOf(id).NameKey });
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            if (id is not (FrequentTab or AllTab))
            {
                header.Children.Add(ThemeResources.Apply(new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center },
                    (Shape.FillProperty, CategoryOf(id).Brush)));
            }

            header.Children.Add(new TextBlock { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Tag = UiTranslator.KeepText });
            var item = new TabItem { Header = header, Tag = id, Style = (Style)FindResource("HomeTabItem"), IsSelected = id == _homeTab };
            AutomationProperties.SetName(item, name);
            HomeTabs.Items.Add(item);
        }

        _buildingTabs = false;
        BuildHomeCards();
    }

    private void OnHomeTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_buildingTabs || e.OriginalSource != HomeTabs || HomeTabs.SelectedItem is not TabItem { Tag: string id })
        {
            return;
        }

        _homeTab = id;
        BuildHomeCards();
    }

    private void SelectHomeTab(string id)
    {
        _homeTab = id;
        ShowHomeTabs();
    }

    private void OnHomeAllToolsClick(object sender, RoutedEventArgs e) => SelectHomeTab(AllTab);

    private bool IsSearching => _homeQuery.Trim().Length > 0;

    /// <summary>The cards of the view chosen: the frequent ones, every category, one category, or the search result.</summary>
    private void BuildHomeCards()
    {
        HomeCards.Children.Clear();
        _homeGrids.Clear();
        var tools = HomeTools();
        var searching = IsSearching;
        var frequent = !searching && _homeTab == FrequentTab;
        HomeDropZone.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        HomeFrequentHeader.Visibility = frequent ? Visibility.Visible : Visibility.Collapsed;
        HomeTabs.Visibility = !searching && !frequent ? Visibility.Visible : Visibility.Collapsed;
        HomeAllToolsLink.Tag = UiTranslator.KeepText;
        HomeAllToolsLink.Content = Loc.F("All {0} tools →", tools.Count);

        if (searching)
        {
            var found = SearchTools(tools, _homeQuery);
            if (found.Count == 0)
            {
                HomeCards.Children.Add(new TextBlock
                {
                    Text = Loc.T("No tools found. Try another word."), FontSize = 15, Margin = new Thickness(0, 24, 0, 24),
                    HorizontalAlignment = HorizontalAlignment.Center, Tag = UiTranslator.KeepText,
                }.Tinted("HomeMutedTextBrush"));
            }
            else
            {
                HomeCards.Children.Add(CardGrid("results", found));
            }
        }
        else if (frequent)
        {
            HomeCards.Children.Add(CardGrid("frequent", FrequentToolIds.Select(id => tools.Single(t => t.Id == id)).ToList()));
        }
        else
        {
            foreach (var category in HomeCategories.Where(c => _homeTab == AllTab || _homeTab == c.Id))
            {
                // Each card once, in its own category. A card also shown in Accounting appears there only when that category is picked.
                var own = tools.Where(t => t.Category == category.Id).ToList();
                if (_homeTab == category.Id && category.Id == "accounting")
                {
                    own.AddRange(tools.Where(t => t.Accounting && t.Category != "accounting"));
                }

                var section = new StackPanel { Margin = new Thickness(0, 0, 0, 25) };
                var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
                heading.Children.Add(ThemeResources.Apply(new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center },
                    (Shape.FillProperty, category.Brush)));
                heading.Children.Add(new TextBlock { Text = Loc.T(category.NameKey), FontSize = 15, FontWeight = FontWeights.Bold, Tag = UiTranslator.KeepText }
                    .Tinted("HomeTextBrush"));
                heading.Children.Add(new TextBlock
                {
                    Text = Loc.F("{0} tools", own.Count), FontSize = 13, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Tag = UiTranslator.KeepText,
                }.Tinted("HomeMutedTextBrush"));
                AutomationProperties.SetName(heading, Loc.T(category.NameKey));
                AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
                section.Children.Add(heading);
                section.Children.Add(CardGrid(category.Id, own));
                HomeCards.Children.Add(section);
            }
        }

        UpdateCardColumns();
        UpdateHomeLayout();
    }

    private UniformGrid CardGrid(string kind, List<HomeTool> tools)
    {
        var grid = new UniformGrid { Tag = kind, Margin = new Thickness(-7, 0, -7, 0) };
        foreach (var tool in tools)
        {
            grid.Children.Add(MakeCard(tool));
        }

        _homeGrids.Add(grid);
        return grid;
    }

    private const double HomeCardTitleLine = 20;

    private Button MakeCard(HomeTool tool)
    {
        var category = CategoryOf(tool.Category);
        var icon = ThemeResources.Apply(new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(10),
            Child = ThemeResources.Apply(new TextBlock
            {
                Style = (Style)FindResource("IconText"),
                Text = tool.Glyph,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }, (TextBlock.ForegroundProperty, category.Brush)),
        }, (Border.BackgroundProperty, category.SoftBrush));
        // Titles may take two lines (a 4-column card is too narrow for the longest Thai and English names);
        // every card reserves room for two, so cards in a row and across sections keep one height.
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = tool.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = HomeCardTitleLine, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = HomeCardTitleLine * 2,
            Tag = UiTranslator.KeepText,
        });
        text.Children.Add(ThemeResources.Apply(new TextBlock
        {
            Text = tool.Text, FontSize = 12, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Tag = UiTranslator.KeepText,
        }, (TextBlock.ForegroundProperty, "HomeMutedTextBrush")));
        var content = new Grid { MinHeight = HomeCardTitleLine * 2 + 18 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(icon);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        // The tooltip adds what the card does not say: the name, then one or two lines of detail (brief 7.1).
        var tip = new StackPanel();
        tip.Children.Add(new TextBlock { Text = tool.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Tag = UiTranslator.KeepText }
            .Tinted("HomeTextBrush"));
        tip.Children.Add(new TextBlock { Text = tool.Tip, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, Tag = UiTranslator.KeepText }
            .Tinted("ToolTipMutedTextBrush"));
        var card = new Button
        {
            Style = (Style)FindResource("HomeCard"), Content = content, Tag = tool.Id,
            ToolTip = new ToolTip { Style = (Style)FindResource("CardToolTip"), Content = tip },
        };
        ToolTipService.SetInitialShowDelay(card, 600);
        AutomationProperties.SetName(card, tool.Title);
        card.Click += OnHomeCardClick;
        return card;
    }

    /// <summary>
    /// 4 columns by 2 rows for the frequent tools (2 by 4 when a card would be under 150 wide); other views as many columns as
    /// fit cards at least 180 wide, up to 4.
    /// </summary>
    private void UpdateCardColumns()
    {
        var width = HomeMain.ActualWidth;
        foreach (var grid in _homeGrids)
        {
            grid.Columns = width <= 0 ? 4
                : (string?)grid.Tag == "frequent" ? ((width - 3 * 14) / 4 >= 150 ? 4 : 2)
                : Math.Clamp((int)((width + 14) / (180 + 14)), 1, 4);
        }
    }

    /// <summary>
    /// Tools matching every word of the search, best first: a word in the name counts most, then the search words of the card,
    /// then its description. Looks in English and Thai, whichever language is shown.
    /// </summary>
    private static List<HomeTool> SearchTools(List<HomeTool> tools, string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        static bool Has(string[] texts, string word) => texts.Any(t => t.Contains(word, StringComparison.OrdinalIgnoreCase));
        return tools.Select((tool, index) =>
            {
                string[] title = [tool.TitleKey, Loc.InThai(tool.TitleKey)];
                string[] keywords = [tool.KeywordsKey, Loc.InThai(tool.KeywordsKey)];
                string[] text = [tool.TextKey, Loc.InThai(tool.TextKey), tool.TipKey, Loc.InThai(tool.TipKey)];
                var score = 0;
                foreach (var word in words)
                {
                    var best = Has(title, word) ? 3 : Has(keywords, word) ? 2 : Has(text, word) ? 1 : 0;
                    if (best == 0) return (tool, index, score: 0);
                    score += best;
                }

                return (tool, index, score);
            })
            .Where(x => x.score > 0).OrderByDescending(x => x.score).ThenBy(x => x.index).Select(x => x.tool).ToList();
    }

    private void OnHomeSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_homeQuery == HomeSearchBox.Text)
        {
            return;
        }

        _homeQuery = HomeSearchBox.Text;
        BuildHomeCards();
    }

    /// <summary>Enter opens the first card found; Esc clears the search and goes back to the view that was open.</summary>
    private void OnHomeSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HomeSearchBox.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Return && IsSearching)
        {
            if (HomeCards.Children.OfType<UniformGrid>().FirstOrDefault()?.Children.OfType<Button>().FirstOrDefault() is { } first)
            {
                first.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }

            e.Handled = true;
        }
    }

    private bool _showingLanguage;

    private void ShowHomeLanguage()
    {
        _showingLanguage = true;
        HomeThai.IsChecked = Loc.Language == Loc.Thai;
        HomeEnglish.IsChecked = Loc.Language != Loc.Thai;
        _showingLanguage = false;
    }

    private void OnHomeLanguageChecked(object sender, RoutedEventArgs e)
    {
        if (!_showingLanguage && HomeThai is not null)
        {
            _viewModel.SetLanguageCommand.Execute(sender == HomeThai ? Loc.Thai : Loc.English);
        }
    }

    private async void OnHomeCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || HomeTools().FirstOrDefault(t => t.Id == id) is not { } tool)
        {
            return;
        }

        var tab = _viewModel;
        switch (id)
        {
            case "combine":
                await tab.CombineFilesCommand.ExecuteAsync(null);
                return;
            case "scanpdf":
                await tab.ScanToPdfCommand.ExecuteAsync(null);
                return;
            case "images":
                await tab.ImagesToPdfCommand.ExecuteAsync(null);
                return;
            case "batch":
                await tab.BatchCommand.ExecuteAsync(null);
                return;
            case "convertscan":
                await tab.ConvertScanCommand.ExecuteAsync(null);
                return;
            case "etax":
                await tab.ETaxInvoiceCommand.ExecuteAsync(null);
                return;
            case "amount":
                tab.AmountWordsCommand.Execute(null);
                return;
        }

        if (tool.NeedsDocument && !tab.HasDocument)
        {
            await tab.OpenCommand.ExecuteAsync(null);
            if (!tab.HasDocument || id == "dwg")
            {
                return; // Cancelled, or opening was the whole job.
            }

            // Let the page show before a dialog comes up over it.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        switch (id)
        {
            case "split":
                await tab.SplitCommand.ExecuteAsync(null);
                break;
            case "scan":
                await tab.CleanUpScanCommand.ExecuteAsync(null);
                break;
            case "pages":
                tab.IsSidebarOpen = true;
                PagesTab.IsChecked = true;
                break;
            case "word":
                await tab.ExportAsync(ExportFormat.Word);
                break;
            case "excel":
                await tab.ExportAsync(ExportFormat.Excel);
                break;
            case "powerpoint":
                await tab.ExportAsync(ExportFormat.PowerPoint);
                break;
            case "pictures":
                await tab.ExportAsync(ExportFormat.Png);
                break;
            case "compress":
                await tab.CompressCommand.ExecuteAsync(null);
                break;
            case "ocr":
                await tab.RecognizeTextCommand.ExecuteAsync(null);
                break;
            case "pdfa":
                await tab.SaveAsPdfACommand.ExecuteAsync(null);
                break;
            case "edittext":
                EditTab.IsChecked = true;
                break;
            case "addtext":
                FillSignTab.IsChecked = true;
                tab.AddTextCommand.Execute(null);
                break;
            case "addpicture":
                FillSignTab.IsChecked = true;
                tab.AddPictureCommand.Execute(null);
                break;
            case "fill":
                FillSignTab.IsChecked = true;
                break;
            case "sign":
                FillSignTab.IsChecked = true;
                await Dispatcher.InvokeAsync(() => OnSignButtonClick(SignButton, new RoutedEventArgs()), DispatcherPriority.Loaded);
                break;
            case "digital":
                await tab.DigitalSignCommand.ExecuteAsync(null);
                break;
            case "stamp":
                tab.StampCommand.Execute(null);
                break;
            case "certified":
                tab.CertifiedCopyCommand.Execute(null);
                break;
            case "comment":
                CommentTab.IsChecked = true;
                tab.CurrentTool = AnnotationTool.Highlight;
                break;
            case "marks":
                await tab.PageMarksCommand.ExecuteAsync(null);
                break;
            case "measure":
                MeasureTab.IsChecked = true;
                break;
            case "count":
                MeasureTab.IsChecked = true;
                tab.CurrentTool = AnnotationTool.Count;
                break;
            case "compare":
                await tab.CompareCommand.ExecuteAsync(null);
                break;
            case "protect":
                await tab.ProtectCommand.ExecuteAsync(null);
                break;
            case "unprotect":
                await tab.RemoveProtectionCommand.ExecuteAsync(null);
                break;
            case "redact":
                RedactTab.IsChecked = true;
                break;
        }
    }

    /// <summary>Shows the start page: this tab's when it has no document, else an empty tab's (made when needed).</summary>
    private void ShowHome()
    {
        if (!_viewModel.HasDocument)
        {
            return;
        }

        if (Tabs.FirstOrDefault(t => !t.HasDocument) is { } empty)
        {
            SwitchTo(empty);
            return;
        }

        var tab = _createTab();
        Tabs.Insert(Tabs.IndexOf(_viewModel) + 1, tab);
        SwitchTo(tab);
    }

    private void OnHomeClick(object sender, RoutedEventArgs e) => ShowHome();

    // --- Layout: tools on the left, recent files on the right --------------------------------------

    /// <summary>Below this width the recent files go under the tools, across the whole page.</summary>
    private const double HomeWideWidth = 1100;

    private const double HomeRecentWidth = 340;
    private const double HomeColumnGap = 24;
    private const double HomeAloneMaxWidth = 900;

    /// <summary>
    /// Wide window and recent files to show: two columns (tools, then 340 wide recent files). Narrower: the recent files below the
    /// tools. No recent files (none yet, all missing, or turned off): the tools alone, at most 900 wide and in the middle.
    /// </summary>
    private void UpdateHomeLayout()
    {
        if (_homeRecent is null)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateHomeLayout);
            return;
        }

        _homeRecent.Suppressed = IsSearching;

        var hasRecent = _homeRecent.HasRows;
        var narrow = HomePage.ActualWidth > 0 && HomePage.ActualWidth < HomeWideWidth;
        if (!hasRecent)
        {
            HomeRecentColumn.Width = new GridLength(0);
            Grid.SetColumn(HomeRecentPanel, 1);
            Grid.SetRow(HomeRecentPanel, 0);
            HomeMain.MaxWidth = IsSearching ? double.PositiveInfinity : HomeAloneMaxWidth; // Stretched but limited: WPF centres it.
            HomeMain.HorizontalAlignment = HorizontalAlignment.Stretch;
            HomeMain.Margin = new Thickness(0);
            HomeRecentPanel.Margin = new Thickness(0);
            return;
        }

        HomeMain.MaxWidth = double.PositiveInfinity;
        HomeMain.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (narrow)
        {
            HomeRecentColumn.Width = new GridLength(0);
            Grid.SetColumn(HomeRecentPanel, 0);
            Grid.SetRow(HomeRecentPanel, 1);
            HomeMain.Margin = new Thickness(0);
            HomeRecentPanel.Margin = new Thickness(0, 4, 0, 0);
        }
        else
        {
            HomeRecentColumn.Width = new GridLength(HomeRecentWidth);
            Grid.SetColumn(HomeRecentPanel, 1);
            Grid.SetRow(HomeRecentPanel, 0);
            HomeMain.Margin = new Thickness(0, 0, HomeColumnGap, 0);
            HomeRecentPanel.Margin = new Thickness(0);
        }
    }

    // --- Recent files ----------------------------------------------------------------------------------

    private void OnHomeRecentToggle(object sender, RoutedEventArgs e) => _homeRecent.ToggleExpanded();

    private async void OnHomeRecentRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
        {
            await _viewModel.OpenRecentCommand.ExecuteAsync(path);
        }
    }

    /// <summary>Right-click: show the file in its folder. Nothing is changed or opened.</summary>
    private void OnHomeRecentOpenFolder(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecentRow row })
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", RevealArguments(row.Path)) { UseShellExecute = false });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                App.LogMessage("Could not open the folder: " + ex.Message);
            }
        }
    }

    /// <summary>The Explorer arguments that select a file in its folder.</summary>
    internal static string RevealArguments(string path) => $"/select,\"{path}\"";

    /// <summary>Right-click: take the file off the list (and its time). The file itself is not touched.</summary>
    private void OnHomeRecentRemove(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecentRow row })
        {
            RecentFiles.Remove(row.Path);
        }
    }

    // --- Drop a file onto the window ---------------------------------------------------------------------

    /// <summary>While a file is dragged over the window the drop zone's frame turns brand red and its fill a very light red.</summary>
    private void SetHomeDropActive(bool active)
    {
        HomeDropFrame.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, active ? "BrandBrush" : "HomeBorderStrongBrush");
        HomeDropFrame.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, active ? "BrandFaintBrush" : "SurfaceBrush");
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e) => SetHomeDropActive(false);
}

internal static class HomeTextBlockExtensions
{
    /// <summary>Sets the colour of a text from a theme brush (a resource reference, so a new theme reaches it).</summary>
    public static TextBlock Tinted(this TextBlock text, string brushKey) => ThemeResources.Apply(text, (TextBlock.ForegroundProperty, brushKey));
}
