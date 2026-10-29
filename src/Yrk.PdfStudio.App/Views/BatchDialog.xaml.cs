using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App.Views;

/// <summary>The files to process, the job, and where the results go.</summary>
public partial class BatchDialog : Window
{
    private const int MaxFiles = 2000;
    private readonly ObservableCollection<RecentFile> _files = [];

    public BatchDialog(IEnumerable<string> initialFiles, BatchAction? action = null)
    {
        InitializeComponent();
        FileList.ItemsSource = _files;
        AddFiles(initialFiles);
        if (_files.Count > 0)
        {
            FolderBox.Text = Path.Combine(_files[0].Folder, Loc.T("Processed"));
        }

        if (action == BatchAction.Stamp)
        {
            StampAction.IsChecked = true;
        }

        Update();
    }

    public BatchRequest? Request { get; private set; }

    private BatchAction Action =>
        CompressAction.IsChecked == true ? BatchAction.Compress
        : MarksAction.IsChecked == true ? BatchAction.PageMarks
        : ProtectAction.IsChecked == true ? BatchAction.Protect
        : ExportAction.IsChecked == true ? BatchAction.Export
        : StampAction.IsChecked == true ? BatchAction.Stamp
        : BatchAction.PdfA;

    private void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (_files.Count >= MaxFiles)
            {
                break;
            }

            if (Directory.Exists(path))
            {
                AddFiles(Directory.EnumerateFiles(path, "*.pdf", SubfoldersBox.IsChecked == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Order());
            }
            else if (File.Exists(path) && Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                     && _files.All(f => !string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                _files.Add(new RecentFile(Path.GetFullPath(path)));
            }
        }

        if (string.IsNullOrWhiteSpace(FolderBox?.Text) && _files.Count > 0 && FolderBox is not null)
        {
            FolderBox.Text = Path.Combine(_files[0].Folder, Loc.T("Processed"));
        }

        Update();
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, CheckFileExists = true, Filter = Loc.T("PDF documents (*.pdf)|*.pdf") };
        if (dialog.ShowDialog(this) == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Add a folder…") };
        if (dialog.ShowDialog(this) == true)
        {
            AddFiles([dialog.FolderName]);
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Save the results in") };
        if (Directory.Exists(FolderBox.Text))
        {
            dialog.InitialDirectory = FolderBox.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        foreach (var file in FileList.SelectedItems.Cast<RecentFile>().ToList())
        {
            _files.Remove(file);
        }

        Update();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _files.Clear();
        Update();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddFiles(paths);
        }
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e) => Update();

    private void OnChanged(object sender, RoutedEventArgs e) => Update();

    private void Update()
    {
        if (OkButton is null || ErrorText is null || FolderBox is null)
        {
            return; // Raised while the window is being built.
        }

        RemoveButton.IsEnabled = FileList.SelectedItems.Count > 0;
        CountText.Text = _files.Count == 1 ? Loc.T("1 file") : Loc.F("{0} files", _files.Count);
        var problem = Problem();
        ErrorText.Text = problem ?? string.Empty;
        OkButton.IsEnabled = problem is null;
    }

    private string? Problem()
    {
        if (_files.Count == 0)
        {
            return Loc.T("Add PDF files or a folder.");
        }

        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return Loc.T("Choose a folder for the results.");
        }

        return null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Problem() is not null)
        {
            return;
        }

        Request = new BatchRequest(_files.Select(f => f.Path).ToList(), Action, FolderBox.Text.Trim());
        DialogResult = true;
    }
}
