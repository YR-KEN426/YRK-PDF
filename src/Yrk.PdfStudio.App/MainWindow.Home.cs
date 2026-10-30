using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
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
    /// <param name="NeedsDocument">False for tools that pick their own files (combine, images to PDF, many files).</param>
    /// <param name="Accounting">Shown in the Accounting group too, besides its own group.</param>
    private sealed record HomeTool(string Id, string Category, string Glyph, string Title, string Text, bool NeedsDocument = true, bool Accounting = false);

    private static readonly Dictionary<string, Color> CategoryColors = new()
    {
        ["organize"] = Color.FromRgb(0xE8, 0x59, 0x0C),
        ["convert"] = Color.FromRgb(0x1A, 0x73, 0xE8),
        ["edit"] = Color.FromRgb(0x7B, 0x4F, 0xC9),
        ["drawings"] = Color.FromRgb(0x00, 0x89, 0x7B),
        ["security"] = Color.FromRgb(0xC6, 0x28, 0x28),
        ["accounting"] = Color.FromRgb(0x2E, 0x7D, 0x32),
    };

    private string _homeCategory = "all";

    /// <summary>The cards, in the language shown (made again when it changes).</summary>
    private static List<HomeTool> HomeTools() =>
    [
        new("combine", "organize", "\uE8F1",
            Loc.T("Combine PDF"), Loc.T("Join several files into one, in the order you choose"), NeedsDocument: false),
        new("split", "organize", "\uE8C6",
            Loc.T("Split PDF"), Loc.T("Save chosen pages, or every few pages, as separate files")),
        new("pages", "organize", "\uE7C3",
            Loc.T("Organize pages"), Loc.T("Turn, delete, move, insert and extract pages")),
        new("scan", "organize", "",
            Loc.T("Clean up scans"), Loc.T("Straighten tilted pages, trim dark edges, delete blank pages"), Accounting: true),
        new("images", "organize", "\uEB9F",
            Loc.T("Images to PDF"), Loc.T("Make one PDF from photos and scans (JPG, PNG)"), NeedsDocument: false),
        new("batch", "organize", "\uE762",
            Loc.T("Process many files"), Loc.T("Compress, convert or mark a whole folder at once"), NeedsDocument: false, Accounting: true),

        new("word", "convert", "\uE8A5",
            Loc.T("PDF to Word"), Loc.T("Editable text with tables and pictures in place")),
        new("excel", "convert", "\uE80A",
            Loc.T("PDF to Excel"), Loc.T("Tables as cells; amounts as numbers you can add up"), Accounting: true),
        new("powerpoint", "convert", "\uE7F4",
            Loc.T("PDF to PowerPoint"), Loc.T("One slide per page, looking just like the PDF")),
        new("pictures", "convert", "\uE722",
            Loc.T("PDF to images"), Loc.T("Each page as a PNG or JPG picture")),
        new("compress", "convert", "\uE7B8",
            Loc.T("Compress PDF"), Loc.T("Make the file smaller for email and chat")),
        new("ocr", "convert", "\uE8FF",
            Loc.T("Recognize text (OCR)"), Loc.T("Make scans searchable and copyable, Thai included"), Accounting: true),
        new("pdfa", "convert", "\uE81C",
            Loc.T("PDF/A for archiving"), Loc.T("The long-term archive format many offices ask for")),
        new("etax", "accounting", "\uE8C7",
            Loc.T("e-Tax Invoice"), Loc.T("Make a tax invoice as PDF/A-3 with the Revenue Department's XML"), NeedsDocument: false),
        new("amount", "accounting", "\uE8EF",
            Loc.T("Amount in words"), Loc.T("Turn 12,500.50 into words in Thai and English"), NeedsDocument: false),

        new("edittext", "edit", "\uE8AC",
            Loc.T("Edit text"), Loc.T("Change words in the PDF itself, Thai included")),
        new("addtext", "edit", "\uE8D2",
            Loc.T("Add text"), Loc.T("Type anywhere on a page, in the font and size you like")),
        new("addpicture", "edit", "\uEB9F",
            Loc.T("Add a picture"), Loc.T("Put a picture on a page, then move and resize it")),
        new("fill", "edit", "\uE73A",
            Loc.T("Fill in a form"), Loc.T("Type in the boxes of a form, or make your own form")),
        new("sign", "edit", "\uE76D",
            Loc.T("Sign"), Loc.T("Place your handwritten signature")),
        new("digital", "edit", "\uE8D7",
            Loc.T("Digital signature"), Loc.T("Sign with a digital ID; later changes show"), Accounting: true),
        new("certified", "edit", "\uEB95",
            Loc.T("Certified true copy"), Loc.T("Stamp \"สำเนาถูกต้อง\" with your signature"), Accounting: true),
        new("stamp", "edit", "",
            Loc.T("Rubber stamp"), Loc.T("Approved, received, paid, with the Thai date, or your company stamp"), Accounting: true),
        new("comment", "edit", "\uE7E6",
            Loc.T("Highlight and comment"), Loc.T("Highlight, underline, notes and drawing")),
        new("marks", "edit", "\uE8A1",
            Loc.T("Watermark and page numbers"), Loc.T("Add a watermark, header, footer or page numbers")),

        new("dwg", "drawings", "\uE81E",
            Loc.T("Open DWG and DXF"), Loc.T("View AutoCAD drawings without AutoCAD")),
        new("measure", "drawings", "\uED5E",
            Loc.T("Measure"), Loc.T("Distances, perimeters and areas at the drawing's scale")),
        new("count", "drawings", "\uE8EF",
            Loc.T("Count items"), Loc.T("Click to count doors, lights or anything, and export the list")),
        new("compare", "drawings", "\uE8AB",
            Loc.T("Compare versions"), Loc.T("See what changed between two revisions of a drawing")),

        new("protect", "security", "\uE72E",
            Loc.T("Protect with password"), Loc.T("Lock the file, or only printing and copying")),
        new("unprotect", "security", "\uE785",
            Loc.T("Remove password"), Loc.T("Save a copy without the password")),
        new("redact", "security", "\uE75C",
            Loc.T("Black out (redact)"), Loc.T("Remove private details for good, not just hide them")),
    ];

    private void InitializeHome()
    {
        // The start page fills the document area, below the tab strip when there is one.
        void KeepBelowTabs() => HomePage.Margin = new Thickness(0, TabStripBar.IsVisible ? TabStripBar.ActualHeight : 0, 0, 0);
        TabStripBar.SizeChanged += (_, _) => KeepBelowTabs();
        TabStripBar.IsVisibleChanged += (_, _) => KeepBelowTabs();

        BuildHomeCards();
        ShowHomeLanguage();
        Loc.LanguageChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            BuildHomeCards();
            ShowHomeLanguage();
        });
    }

    private void BuildHomeCards()
    {
        HomeCards.Children.Clear();
        foreach (var tool in HomeTools().Where(t => _homeCategory == "all" || t.Category == _homeCategory || (t.Accounting && _homeCategory == "accounting")).OrderBy(t => _homeCategory == t.Category ? 0 : 1))
        {
            var color = CategoryColors[tool.Category];
            var icon = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Style = (Style)FindResource("IconText"),
                    Text = tool.Glyph,
                    FontSize = 17,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var content = new DockPanel();
            DockPanel.SetDock(icon, Dock.Top);
            content.Children.Add(icon);
            var title = new TextBlock { Text = tool.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 10, 0, 2), Tag = UiTranslator.KeepText };
            DockPanel.SetDock(title, Dock.Top);
            content.Children.Add(title);
            content.Children.Add(new TextBlock
            {
                Text = tool.Text,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12,
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
                Tag = UiTranslator.KeepText,
            });

            var card = new Button { Style = (Style)FindResource("HomeCard"), Content = content, Tag = tool.Id, ToolTip = tool.Text };
            AutomationProperties.SetName(card, tool.Title);
            card.Click += OnHomeCardClick;
            HomeCards.Children.Add(card);
        }
    }

    private void OnHomeCategoryChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string category } && HomeCards is not null)
        {
            _homeCategory = category;
            BuildHomeCards();
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
            case "images":
                await tab.ImagesToPdfCommand.ExecuteAsync(null);
                return;
            case "batch":
                await tab.BatchCommand.ExecuteAsync(null);
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
}
