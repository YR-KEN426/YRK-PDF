using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Views;

public partial class SplitDialog : Window
{
    private readonly int _pageCount;
    private readonly Brush _normalBrush;
    private readonly IReadOnlyList<PdfBookmark> _bookmarks;

    public SplitDialog(int pageCount, string folder, IReadOnlyList<PdfBookmark> bookmarks)
    {
        InitializeComponent();
        _pageCount = pageCount;
        _bookmarks = bookmarks;
        _normalBrush = Preview.Foreground;
        Description.Text = pageCount == 1 ? Loc.T("This document has 1 page. The original file is not changed.") : Loc.F("This document has {0} pages. The original file is not changed.", pageCount);
        FolderInput.Text = folder;
        var depth = Math.Min(PageRanges.BookmarkDepth(bookmarks), 4);
        if (depth > 0 && pageCount > 1)
        {
            BookmarkMode.Visibility = BookmarkHint.Visibility = Visibility.Visible;
            LevelInput.ItemsSource = Enumerable.Range(1, depth).ToList();
            LevelInput.SelectedIndex = 0;
            BookmarkMode.IsChecked = true; // A file with chapters is usually split by them.
        }

        UpdatePreview();
    }

    private void OnLevelChanged(object sender, RoutedEventArgs e)
    {
        if (BookmarkMode is not null)
        {
            BookmarkMode.IsChecked = true;
        }

        UpdatePreview();
    }

    public SplitPlan? Plan { get; private set; }

    private void OnChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void OnRangeTextChanged(object sender, RoutedEventArgs e)
    {
        if (RangeMode is not null && RangeInput.Text.Length > 0)
        {
            RangeMode.IsChecked = true;
        }

        UpdatePreview();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = FolderInput.Text, Title = Loc.T("Save the parts to") };
        if (dialog.ShowDialog(this) == true)
        {
            FolderInput.Text = dialog.FolderName;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (TryBuildPlan(out var plan, out _))
        {
            Plan = plan;
            DialogResult = true;
        }
    }

    private void UpdatePreview()
    {
        if (OkButton is null)
        {
            return; // Still loading.
        }

        var valid = TryBuildPlan(out var plan, out var error);
        OkButton.IsEnabled = valid;
        Preview.Foreground = valid ? _normalBrush : (Brush)FindResource("ErrorBrush");
        Preview.Text = valid
            ? Loc.F(plan!.Parts.Count == 1 ? "Creates 1 file: " : "Creates {0} files: ", plan.Parts.Count) +
              string.Join("; ", plan.Parts.Take(6).Select((p, i) => Part(plan, i, p))) +
              (plan.Parts.Count > 6 ? "; …" : string.Empty)
            : error;
    }

    private static string Part(SplitPlan plan, int index, IReadOnlyList<int> pages)
    {
        var range = Loc.F("pages {0}", ViewModels.MainViewModel.FormatPageList(pages));
        return plan.Titles?[index] is { Length: > 0 } title ? $"{title} ({range})" : range;
    }

    private bool TryBuildPlan(out SplitPlan? plan, out string error)
    {
        plan = null;
        error = string.Empty;
        try
        {
            IReadOnlyList<IReadOnlyList<int>> parts;
            IReadOnlyList<string>? titles = null;
            if (BookmarkMode.IsChecked == true)
            {
                var chapters = PageRanges.ByBookmarks(_bookmarks, _pageCount, LevelInput.SelectedItem as int? ?? 1);
                parts = chapters.Select(c => c.Pages).ToList();
                titles = chapters.Select(c => c.Title).ToList();
            }
            else if (EveryMode.IsChecked == true)
            {
                if (!int.TryParse(EveryInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 1)
                {
                    error = Loc.T("Enter how many pages each file should have.");
                    return false;
                }

                parts = PageRanges.EveryNPages(_pageCount, size);
            }
            else
            {
                parts = PageRanges.Parse(RangeInput.Text, _pageCount);
            }

            if (!Directory.Exists(FolderInput.Text))
            {
                error = Loc.T("Choose an existing folder.");
                return false;
            }

            plan = new SplitPlan(FolderInput.Text, parts, titles);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
