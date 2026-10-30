using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// Pictures added to a page from a file, the clipboard or by dropping them on the window. They are placed items like
/// signatures: moved and resized (keeping their proportions) until the document is saved.
/// </summary>
public sealed partial class MainViewModel
{
    private IRelayCommand? _addPictureCommand;

    public IRelayCommand AddPictureCommand => _addPictureCommand ??= new RelayCommand(AddPicture);

    private void AddPicture()
    {
        if (_document is null || _dialogs.PickImageFile("Choose a picture") is not { } path || LoadPicture(path) is not { } picture)
        {
            return;
        }

        BeginPlacement(new Placement(PlacedItemKind.Picture, picture, null, "Click where the picture should go. Press Esc to cancel."));
    }

    /// <summary>Puts a dropped picture file on the current page.</summary>
    public void PlacePictureFile(string path)
    {
        if (LoadPicture(path) is { } picture)
        {
            PlacePicture(picture);
        }
    }

    /// <summary>Puts the picture on the clipboard on the current page.</summary>
    /// <returns>Whether there was a picture to paste.</returns>
    public bool PastePicture()
    {
        BitmapSource? picture;
        try
        {
            picture = PictureImaging.FromClipboard();
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or System.Runtime.InteropServices.ExternalException)
        {
            return false; // Another program holds the clipboard, or it has no readable picture.
        }

        if (picture is null)
        {
            return false;
        }

        PlacePicture(picture);
        return true;
    }

    private void PlacePicture(BitmapSource picture)
    {
        if (_document is null || _currentPageIndex >= Pages.Count)
        {
            return;
        }

        var page = Pages[_currentPageIndex];
        var (width, height) = PictureImaging.FitToPage(picture, page.WidthPoints, page.HeightPoints);
        var item = PlacedItemViewModel.CreateImage(page.Index, PlacedItemKind.Picture, page.WidthPoints, page.HeightPoints, picture);

        // In the middle of what can be seen of the page: at a high zoom its own middle may be off the screen.
        var seen = (_scrollOffset + (_viewportHeight / 2) - _pageTops[page.Index]) / Math.Max(1, page.DisplayHeight);
        item.Bounds = item.ToBounds((page.WidthPoints - width) / 2, (Math.Clamp(seen, 0, 1) * page.HeightPoints) - (height / 2), width, height);
        AttachItem(item);
        _undo.Push(() => DetachItem(item), () => AttachItem(item));
        SelectItem(item);
    }

    private BitmapSource? LoadPicture(string path)
    {
        try
        {
            return PictureImaging.Load(path);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(ex.Message);
            return null;
        }
    }
}
