using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

public sealed class BookmarkViewModel
{
    private BookmarkViewModel(string title, int? pageIndex, IReadOnlyList<BookmarkViewModel> children)
    {
        Title = title;
        PageIndex = pageIndex;
        Children = children;
    }

    public string Title { get; }

    public int? PageIndex { get; }

    public IReadOnlyList<BookmarkViewModel> Children { get; }

    public static BookmarkViewModel From(PdfBookmark bookmark) =>
        new(
            string.IsNullOrWhiteSpace(bookmark.Title) ? "(untitled)" : bookmark.Title,
            bookmark.PageIndex,
            bookmark.Children.Select(From).ToList());
}
