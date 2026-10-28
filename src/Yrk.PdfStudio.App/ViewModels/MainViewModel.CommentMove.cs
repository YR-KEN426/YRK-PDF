using System.Windows;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// Moving and resizing comments already on the page (Select tool): drag a comment to move it, or a corner
/// of the selected one to resize it. Text markups stay on their text; notes and text boxes only move.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>How near (screen DIPs) a press must be to a corner of the selection to resize.</summary>
    private const double CommentHandleRadiusDips = 7;

    /// <summary>The smallest size a comment can be resized to, in screen DIPs.</summary>
    private const double CommentMinimumDips = 6;

    private CommentDrag? _commentDrag;

    private static bool IsMovable(PdfAnnotationKind kind) =>
        kind is not (PdfAnnotationKind.Highlight or PdfAnnotationKind.Underline or PdfAnnotationKind.Squiggly or PdfAnnotationKind.StrikeOut
            or PdfAnnotationKind.Caret or PdfAnnotationKind.Popup or PdfAnnotationKind.Unknown);

    private static bool IsResizable(PdfAnnotationKind kind) =>
        kind is PdfAnnotationKind.Square or PdfAnnotationKind.Circle or PdfAnnotationKind.Ink or PdfAnnotationKind.Line
            or PdfAnnotationKind.Polygon or PdfAnnotationKind.PolyLine or PdfAnnotationKind.Stamp;

    /// <summary>
    /// A press with the Select tool: on a corner of the selected comment it starts resizing, on a movable
    /// comment it selects it and starts moving. Anywhere else the press selects text as usual.
    /// </summary>
    private void BeginCommentDrag(PageViewModel page, NormalizedPoint point)
    {
        _commentDrag = null;
        if (SelectedComment is { } selected && selected.PageIndex == page.Index && IsResizable(selected.Annotation.Kind)
            && CornerAt(page, selected.Annotation.Bounds, point) is { } corner)
        {
            _commentDrag = new CommentDrag(selected, corner, point);
            return;
        }

        var target = SelectedComment is { } current && current.PageIndex == page.Index && Contains(current.Annotation.Bounds, point)
            ? current
            : HitTestComment(page, point);
        if (target is null || !IsMovable(target.Annotation.Kind))
        {
            return;
        }

        SelectComment(target);
        _commentDrag = new CommentDrag(target, -1, point);
    }

    /// <summary>Shows where the comment will go; the page itself is redrawn when the mouse is released.</summary>
    private void UpdateCommentDrag(CommentDrag drag, PageViewModel page, NormalizedPoint point)
    {
        var b = DraggedBounds(drag, page, point);
        page.AnnotationSelection = new Rect(b.X * page.WidthPoints, b.Y * page.HeightPoints, b.Width * page.WidthPoints, b.Height * page.HeightPoints);
    }

    private async Task EndCommentDragAsync(CommentDrag drag, PageViewModel page, NormalizedPoint point, bool isClick)
    {
        if (isClick || _document is not { } document)
        {
            SelectedComment = null; // Puts the box back where the comment is.
            SelectComment(drag.Comment);
            return;
        }

        var before = drag.Comment.Annotation.Bounds;
        var after = DraggedBounds(drag, page, point);
        var reference = drag.Comment.Reference;
        await RunAnnotationChangeAsync(reference.PageIndex, () => document.MoveAnnotationAsync(reference, after), keepSelectionId: reference.Id);
        _undo.Push(
            () => _ = RunAnnotationChangeAsync(reference.PageIndex, () => document.MoveAnnotationAsync(reference, before), keepSelectionId: reference.Id),
            () => _ = RunAnnotationChangeAsync(reference.PageIndex, () => document.MoveAnnotationAsync(reference, after), keepSelectionId: reference.Id));
    }

    /// <summary>The comment's bounds after the drag so far, kept on the page and not smaller than a few pixels.</summary>
    private NormalizedRect DraggedBounds(CommentDrag drag, PageViewModel page, NormalizedPoint point)
    {
        var b = drag.Comment.Annotation.Bounds;
        var dx = point.X - drag.Start.X;
        var dy = point.Y - drag.Start.Y;
        if (drag.Corner < 0)
        {
            var x = Math.Clamp(b.X + dx, 0, Math.Max(0, 1 - b.Width));
            var y = Math.Clamp(b.Y + dy, 0, Math.Max(0, 1 - b.Height));
            return new NormalizedRect(x, y, b.Width, b.Height);
        }

        // The opposite corner stays put.
        double left = b.X, top = b.Y, right = b.X + b.Width, bottom = b.Y + b.Height;
        var minWidth = CommentMinimumDips * OverlayUnit / page.WidthPoints;
        var minHeight = CommentMinimumDips * OverlayUnit / page.HeightPoints;
        if (drag.Corner is 0 or 3)
        {
            left = Math.Clamp(left + dx, 0, right - minWidth);
        }
        else
        {
            right = Math.Clamp(right + dx, left + minWidth, 1);
        }

        if (drag.Corner is 0 or 1)
        {
            top = Math.Clamp(top + dy, 0, bottom - minHeight);
        }
        else
        {
            bottom = Math.Clamp(bottom + dy, top + minHeight, 1);
        }

        return new NormalizedRect(left, top, right - left, bottom - top);
    }

    /// <summary>The corner of the bounds under the point (0 top-left, 1 top-right, 2 bottom-right, 3 bottom-left).</summary>
    private int? CornerAt(PageViewModel page, NormalizedRect b, NormalizedPoint point)
    {
        var radius = CommentHandleRadiusDips * OverlayUnit;
        NormalizedPoint[] corners = [new(b.X, b.Y), new(b.X + b.Width, b.Y), new(b.X + b.Width, b.Y + b.Height), new(b.X, b.Y + b.Height)];
        for (var i = 0; i < corners.Length; i++)
        {
            if (Math.Abs(corners[i].X - point.X) * page.WidthPoints <= radius && Math.Abs(corners[i].Y - point.Y) * page.HeightPoints <= radius)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>A comment being moved (Corner -1) or resized by one corner.</summary>
    private sealed record CommentDrag(CommentViewModel Comment, int Corner, NormalizedPoint Start);
}
