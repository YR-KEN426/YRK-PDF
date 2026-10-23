using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Views;

public enum FileListMode
{
    CombinePdfs,
    ImagesToPdf,
}

/// <summary>An ordered list of files to combine into one PDF, or images to turn into one.</summary>
public partial class FileListDialog : Window
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".webp"];

    private readonly FileListMode _mode;
    private readonly ObservableCollection<FileEntry> _files = [];

    public FileListDialog(FileListMode mode, IEnumerable<string> initialFiles)
    {
        InitializeComponent();
        _mode = mode;
        FileList.ItemsSource = _files;

        if (mode == FileListMode.CombinePdfs)
        {
            Title = "Combine files";
            Heading.Text = "Combine PDFs into one";
            Description.Text = "Add PDFs and put them in order. Pages are copied in this order into a new file; the originals are not changed.";
            OkButton.Content = "Combine";
            ImageOptions.Visibility = Visibility.Collapsed;
        }
        else
        {
            Title = "Images to PDF";
            Heading.Text = "Create a PDF from images";
            Description.Text = "Photos of documents, scans or screenshots. Each image becomes one page; phone photos are turned upright automatically.";
            OkButton.Content = "Create PDF";
        }

        AddFiles(initialFiles);
        UpdateButtons();
    }

    public IReadOnlyList<string> Files => _files.Select(f => f.Path).ToList();

    public ImagesToPdfOptions ImageOptionsResult => new(PageSizeChoice.SelectedIndex switch
    {
        1 => PdfPageSizeMode.Letter,
        2 => PdfPageSizeMode.MatchImage,
        _ => PdfPageSizeMode.A4,
    });

    private int MinimumFiles => _mode == FileListMode.CombinePdfs ? 2 : 1;

    private void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Accepts(path) && _files.All(f => !string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                _files.Add(new FileEntry(path, _mode == FileListMode.ImagesToPdf ? LoadThumbnail(path) : null));
            }
        }

        UpdateButtons();
    }

    private bool Accepts(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return File.Exists(path) && (_mode == FileListMode.CombinePdfs ? extension == ".pdf" : ImageExtensions.Contains(extension));
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            CheckFileExists = true,
            Filter = _mode == FileListMode.CombinePdfs
                ? Loc.T("PDF documents (*.pdf)|*.pdf")
                : Loc.T("Images") + "|" + string.Join(";", ImageExtensions.Select(x => "*" + x)) + "|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        foreach (var entry in FileList.SelectedItems.Cast<FileEntry>().ToList())
        {
            _files.Remove(entry);
        }

        UpdateButtons();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int direction)
    {
        if (FileList.SelectedItem is not FileEntry entry)
        {
            return;
        }

        var index = _files.IndexOf(entry);
        var target = index + direction;
        if (target >= 0 && target < _files.Count)
        {
            _files.Move(index, target);
            FileList.SelectedItem = entry;
        }
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e) => UpdateButtons();

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

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void UpdateButtons()
    {
        if (OkButton is null)
        {
            return;
        }

        OkButton.IsEnabled = _files.Count >= MinimumFiles;
        RemoveButton.IsEnabled = FileList.SelectedItems.Count > 0;
        UpButton.IsEnabled = DownButton.IsEnabled = FileList.SelectedItems.Count == 1;
        Summary.Text = _files.Count == 0
            ? Loc.T("Drag files here or choose Add files.")
            : _files.Count == 1 ? Loc.T("1 file") : Loc.F("{0} files", _files.Count);
    }

    private static ImageSource? LoadThumbnail(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 88;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or FileFormatException or UnauthorizedAccessException)
        {
            return null; // The file will still be tried when the PDF is created.
        }
    }

    public sealed record FileEntry(string Path, ImageSource? Thumbnail)
    {
        public string Name => System.IO.Path.GetFileName(Path);

        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    }
}
