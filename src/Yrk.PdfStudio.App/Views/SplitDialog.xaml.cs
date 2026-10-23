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

    public SplitDialog(int pageCount, string folder)
    {
        InitializeComponent();
        _pageCount = pageCount;
        _normalBrush = Preview.Foreground;
        Description.Text = pageCount == 1 ? Loc.T("This document has 1 page. The original file is not changed.") : Loc.F("This document has {0} pages. The original file is not changed.", pageCount);
        FolderInput.Text = folder;
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
              string.Join("; ", plan.Parts.Take(6).Select(p => Loc.F("pages {0}", ViewModels.MainViewModel.FormatPageList(p)))) +
              (plan.Parts.Count > 6 ? "; …" : string.Empty)
            : error;
    }

    private bool TryBuildPlan(out SplitPlan? plan, out string error)
    {
        plan = null;
        error = string.Empty;
        try
        {
            IReadOnlyList<IReadOnlyList<int>> parts;
            if (EveryMode.IsChecked == true)
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

            plan = new SplitPlan(FolderInput.Text, parts);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
