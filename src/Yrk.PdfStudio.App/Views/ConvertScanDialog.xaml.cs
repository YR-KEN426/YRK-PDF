using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf.Ocr;

namespace Yrk.PdfStudio.App.Views;

public partial class ConvertScanDialog : Window
{
    private const int MaxFiles = 2000;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp" };
    private readonly ObservableCollection<RecentFile> _files = [];
    private readonly List<OcrDialog.LanguageChoice> _choices = [];

    public ConvertScanDialog(IEnumerable<string> initialFiles)
    {
        InitializeComponent();
        WindowSizing.FitToWorkArea(this);
        FileList.ItemsSource = _files;
        var data = new OcrLanguageData();
        _choices.AddRange(OcrLanguageData.Languages.Select(l => new OcrDialog.LanguageChoice(l.Code, l.Name,
            data.IsInstalled(l.Code) ? string.Empty : " (download)")
            { IsChecked = l.Code is "tha" or "eng" }));
        LanguageList.ItemsSource = _choices;
        AddFiles(initialFiles);
        Loaded += (_, _) => FileList.Focus();
    }

    public ConvertScanRequest? Request { get; private set; }

    private void AddFiles(IEnumerable<string> paths)
    {
        try
        {
            foreach (var path in paths)
            {
                if (_files.Count >= MaxFiles) break;
                if (Directory.Exists(path))
                {
                    var options = new EnumerationOptions { RecurseSubdirectories = SubfoldersBox.IsChecked == true,
                        IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    AddFiles(Directory.EnumerateFiles(path, "*", options).Where(p => Extensions.Contains(Path.GetExtension(p))).Order());
                }
                else if (File.Exists(path) && Extensions.Contains(Path.GetExtension(path)))
                {
                    var full = Path.GetFullPath(path);
                    if (_files.All(f => !string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase)))
                        _files.Add(new RecentFile(full));
                }
            }
            Update();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Update();
            ErrorText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ErrorBrush");
            var error = ErrorCatalog.ConvertScanAddFiles;
            App.LogError(error, ex);
            ErrorText.Text = Loc.T(error.Message) + "\n" + Loc.T(error.NextStep);
        }
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, CheckFileExists = true,
            Filter = Loc.T("PDF and images (*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp)|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp") };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Add a folder…") };
        if (dialog.ShowDialog(this) == true) AddFiles([dialog.FolderName]);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Save the results in") };
        if (Directory.Exists(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        foreach (var file in FileList.SelectedItems.Cast<RecentFile>().ToArray()) _files.Remove(file);
        Update();
    }

    private void OnFileKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        OnRemove(sender, e);
        e.Handled = true;
    }

    private void OnClear(object sender, RoutedEventArgs e) { _files.Clear(); Update(); }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths);
    }
    private void OnSelectionChanged(object sender, RoutedEventArgs e) => Update();
    private void OnChanged(object sender, RoutedEventArgs e)
    {
        // Checkbox bindings update after their routed event; validate once the new value is stored.
        Dispatcher.BeginInvoke(new Action(Update));
    }

    private void Update()
    {
        if (OkButton is null || FolderBox is null || ErrorText is null) return;
        RemoveButton.IsEnabled = FileList.SelectedItems.Count > 0;
        EmptyFileHint.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = _files.Count == 1 ? Loc.T("1 file") : Loc.F("{0} files", _files.Count);
        var problem = Problem();
        ErrorText.Text = problem ?? string.Empty;
        ErrorText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            _files.Count == 0 ? "SecondaryTextBrush" : "ErrorBrush");
        OkButton.IsEnabled = problem is null;
    }

    private string? Problem()
    {
        if (_files.Count == 0) return Loc.T("Add PDF files, images or a folder.");
        if (!_choices.Any(c => c.IsChecked)) return Loc.T("Choose at least one language.");
        if (PdfBox.IsChecked != true && WordBox.IsChecked != true && TextBoxOutput.IsChecked != true)
            return Loc.T("Choose at least one output format.");
        var folder = FolderBox.Text.Trim();
        if (folder.Length > 0 && (!Path.IsPathFullyQualified(folder) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0))
            return Loc.T("Choose a folder for the results.");
        return null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Update();
        if (Problem() is not null) return;
        Request = new ConvertScanRequest(_files.Select(f => f.Path).ToArray(), _choices.Where(c => c.IsChecked).Select(c => c.Code).ToArray(),
            AllPages.IsChecked == true ? OcrPageSelection.AllPages : OcrPageSelection.PagesWithoutText,
            PdfBox.IsChecked == true, WordBox.IsChecked == true, TextBoxOutput.IsChecked == true, ReviewBox.IsChecked == true,
            FolderBox.Text.Trim());
        DialogResult = true;
    }
}
