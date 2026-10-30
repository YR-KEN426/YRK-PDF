using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Forms;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>Comment tools: text selection, highlights, notes, pen, shapes, and the comments list.</summary>
public sealed partial class MainViewModel
{
    private const double ClickTolerance = 3; // points: smaller drags count as a click

    private static readonly Brush SelectionFill = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x33, 0x99, 0xFF)));

    private AnnotationTool _currentTool = AnnotationTool.Select;
    private Color _annotationColor = Palette[0];
    private double _strokeWidth = 2;
    private CommentViewModel? _selectedComment;
    private PdfTextSelection? _textSelection;
    private PageViewModel? _textSelectionPage;
    private Gesture? _gesture;
    private bool _selectionRequestRunning;
    private IRelayCommand<Color>? _setColorCommand;
    private int _commentsGeneration;

    /// <summary>Raised after a note is created, so the view can focus its text in the comments panel.</summary>
    public event EventHandler<CommentViewModel>? CommentEditRequested;

    public static IReadOnlyList<Color> Palette { get; } =
    [
        Color.FromRgb(0xFF, 0xE1, 0x35), // yellow
        Color.FromRgb(0x7E, 0xE0, 0x81), // green
        Color.FromRgb(0x64, 0xB5, 0xF6), // blue
        Color.FromRgb(0xF4, 0x8F, 0xB1), // pink
        Color.FromRgb(0xE5, 0x39, 0x35), // red
        Color.FromRgb(0x21, 0x21, 0x21), // black
    ];

    public static IReadOnlyList<double> StrokeWidths { get; } = [1, 2, 4];

    public ObservableCollection<CommentViewModel> Comments { get; } = [];

    public IRelayCommand<Color> SetColorCommand => _setColorCommand ??= new RelayCommand<Color>(color => AnnotationColor = color);

    public AnnotationTool CurrentTool
    {
        get => _currentTool;
        set
        {
            if (ChangesTheFile(value) && !ConfirmFirstEdit())
            {
                // Cancelled: the toolbar goes back to the tool in use once this change has finished.
                App.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(CurrentTool)));
                return;
            }

            if (value == AnnotationTool.Highlight && _yellowBeforeDrawing)
            {
                _yellowBeforeDrawing = false; // The Insert menu chose black for drawing: the highlighter goes back to yellow.
                AnnotationColor = Palette[0];
            }

            var leftTextEditing = _currentTool == AnnotationTool.EditText && value != AnnotationTool.EditText;
            if (SetProperty(ref _currentTool, value))
            {
                if (leftTextEditing)
                {
                    _ = ApplyTextEditsNowAsync(); // Another tool finishes the changed lines.
                }

                EndPlacement();
                CancelMeasurementDraft();
                ClearTextSelection();
                OnPropertyChanged(nameof(IsMeasuring));
                OnPropertyChanged(nameof(IsCounting));
                OnPropertyChanged(nameof(IsEditingFields));
                UpdateTextStyleTarget();
                if (value == AnnotationTool.Count)
                {
                    UpdateCountSummary();
                }
            }
        }
    }

    public Color AnnotationColor
    {
        get => _annotationColor;
        set
        {
            if (SetProperty(ref _annotationColor, value))
            {
                OnPropertyChanged(nameof(AnnotationBrush));
            }
        }
    }

    public Brush AnnotationBrush => Frozen(new SolidColorBrush(_annotationColor));

    public double StrokeWidth
    {
        get => _strokeWidth;
        set => SetProperty(ref _strokeWidth, Math.Clamp(value, 0.5, 12));
    }

    public bool HasTextSelection => _textSelection is not null;

    public bool HasComments => Comments.Count > 0;

    public CommentViewModel? SelectedComment
    {
        get => _selectedComment;
        set
        {
            if (_selectedComment == value)
            {
                return;
            }

            if (_selectedComment is not null)
            {
                _selectedComment.IsSelected = false;
                if (_selectedComment.PageIndex < Pages.Count)
                {
                    Pages[_selectedComment.PageIndex].AnnotationSelection = null;
                }
            }

            _selectedComment = value;
            OnPropertyChanged();
            if (value is null || value.PageIndex >= Pages.Count)
            {
                return;
            }

            value.IsSelected = true;
            var page = Pages[value.PageIndex];
            var b = value.Annotation.Bounds;
            page.AnnotationSelection = new Rect(b.X * page.WidthPoints, b.Y * page.HeightPoints, b.Width * page.WidthPoints, b.Height * page.HeightPoints);
            page.IsAnnotationResizable = IsResizable(value.Annotation.Kind);
            if (value.PageIndex != CurrentPageIndex)
            {
                GoToPage(value.PageIndex);
            }
        }
    }

    // --- Gestures (called by the view; points are page-relative 0..1) ----------------------------

    /// <summary>Starts a drag or click on a page. Returns true when the view should capture the mouse.</summary>
    public bool BeginPageGesture(PageViewModel page, NormalizedPoint point)
    {
        if (_document is not null && !IsBusy && _pendingDigitalSign is not null)
        {
            _gesture = new Gesture(page, point, [point]);
            return true;
        }

        if (_document is null || IsBusy || _placement is not null)
        {
            return false;
        }

        if (_newFields.Count > 0 && !IsFieldTool(_currentTool))
        {
            _ = ApplyNewFieldsAsync(); // A click outside the new fields finishes them.
            return false;
        }

        if (_activeTextEdit is { IsEditing: true })
        {
            CommitEditsRequested?.Invoke(this, EventArgs.Empty); // Ends the line being typed, so its change counts below.
        }

        if (HasTextEdits && !(_currentTool == AnnotationTool.EditText && _textEdits.Any(e => e.Page == page && e.Contains(point))))
        {
            _ = ApplyTextEditsNowAsync(); // A click outside the changed lines finishes them.
            return false;
        }

        SelectItem(null);
        ClearTextSelection();
        var pressed = point;
        if (IsMeasuring && !IsCounting)
        {
            BeginMeasureGesture(page, point);
            point = Snap(page, point); // A measurement starts on the drawing point under the cursor.
        }

        _commentDrag = null;
        if (_currentTool == AnnotationTool.Select)
        {
            BeginCommentDrag(page, point);
        }

        _gesture = new Gesture(page, point, [point]) { Pressed = pressed };
        return true;
    }

    public void UpdatePageGesture(NormalizedPoint point)
    {
        if (_gesture is not { } gesture)
        {
            return;
        }

        gesture.Points.Add(point);
        gesture.Latest = point;
        if (_pendingDigitalSign is not null)
        {
            UpdateDigitalPlacementPreview(gesture, point);
            return;
        }

        if (_measureEdit is { } edit)
        {
            UpdateMeasureEdit(edit, point);
            return;
        }

        if (_commentDrag is { } drag)
        {
            UpdateCommentDrag(drag, gesture.Page, point);
            return;
        }

        var page = gesture.Page;
        var brush = Frozen(new SolidColorBrush(_annotationColor));
        switch (_currentTool)
        {
            case AnnotationTool.Select or AnnotationTool.Highlight or AnnotationTool.Underline or AnnotationTool.StrikeOut:
                _ = RequestSelectionPreviewAsync(gesture);
                break;
            case AnnotationTool.Pen:
                page.SetPreview(Polyline(page, gesture.Points), null, brush, _strokeWidth);
                break;
            case AnnotationTool.Rectangle:
                page.SetPreview(new RectangleGeometry(ToPoints(page, gesture.Start, point)), null, brush, _strokeWidth);
                break;
            case var tool when IsFieldTool(tool):
                page.SetPreview(new RectangleGeometry(ToPoints(page, gesture.Start, point)), FieldPreviewFill, FieldPreviewStroke, 1);
                break;
            case AnnotationTool.Ellipse:
                page.SetPreview(new EllipseGeometry(ToPoints(page, gesture.Start, point)), null, brush, _strokeWidth);
                break;
            case AnnotationTool.Arrow:
                page.SetPreview(ArrowGeometry(ToPoint(page, gesture.Start), ToPoint(page, Constrain(page, gesture.Start, point)), _strokeWidth), null, brush, _strokeWidth);
                break;
            case AnnotationTool.Line:
                page.SetPreview(new LineGeometry(ToPoint(page, gesture.Start), ToPoint(page, Constrain(page, gesture.Start, point))), null, brush, _strokeWidth);
                break;
            case AnnotationTool.Redact:
                UpdateRedactionPreview(gesture, point);
                break;
            case AnnotationTool.MeasureDistance or AnnotationTool.Calibrate:
                UpdateMeasureDrag(gesture, point);
                break;
        }
    }

    public void CancelPageGesture()
    {
        CancelMeasureEdit();
        if (_commentDrag is { } drag)
        {
            _commentDrag = null;
            SelectedComment = null; // Puts the box back where the comment is.
            SelectComment(drag.Comment);
        }

        _gesture?.Page.ClearPreview();
        _gesture = null;
    }

    public async Task EndPageGesture(NormalizedPoint point)
    {
        if (_gesture is not { } gesture || _document is not { } document)
        {
            return;
        }

        _gesture = null;
        var page = gesture.Page;
        // Measured from where the mouse went down: a snapped start can be a few pixels away.
        var isClick = Distance(page, gesture.Pressed ?? gesture.Start, point) < ClickTolerance;
        page.ClearPreview();
        if (_pendingDigitalSign is not null)
        {
            await EndDigitalPlacementAsync(page, gesture.Start, point, isClick);
            return;
        }

        if (_measureEdit is { } edit)
        {
            _measureEdit = null;
            await EndMeasureEditAsync(edit);
            return;
        }

        if (_commentDrag is { } drag)
        {
            _commentDrag = null;
            await EndCommentDragAsync(drag, page, point, isClick);
            return;
        }

        var color = new PdfColor(_annotationColor.R, _annotationColor.G, _annotationColor.B);

        switch (_currentTool)
        {
            case AnnotationTool.Select when isClick:
            case AnnotationTool.Highlight or AnnotationTool.Underline or AnnotationTool.StrikeOut when isClick:
                SelectComment(HitTestComment(page, point));
                break;

            case AnnotationTool.Select:
                var selection = await SafeSelectAsync(document, page, gesture.Start, point);
                if (selection is not null)
                {
                    _textSelection = selection;
                    _textSelectionPage = page;
                    page.SetPreview(RectsGeometry(page, MergeByLine(selection.Rects)), SelectionFill, null, 0);
                    OnPropertyChanged(nameof(HasTextSelection));
                }

                break;

            case AnnotationTool.Highlight or AnnotationTool.Underline or AnnotationTool.StrikeOut:
                if (await SafeSelectAsync(document, page, gesture.Start, point) is { Rects.Count: > 0 } marked)
                {
                    await AddAnnotationAsync(page, new PdfTextMarkupSpec(MarkupKind(_currentTool), MergeByLine(marked.Rects), color, Author: Author));
                }

                break;

            case AnnotationTool.Note:
                var id = await AddAnnotationAsync(page, new PdfNoteSpec(point, color, Author: Author));
                if (id is not null && Comments.FirstOrDefault(c => c.Annotation.Id == id) is { } note)
                {
                    SelectComment(note);
                    CommentEditRequested?.Invoke(this, note);
                }

                break;

            case AnnotationTool.Pen when gesture.Points.Count >= 2:
                await AddAnnotationAsync(page, new PdfInkSpec([Simplify(page, gesture.Points)], _strokeWidth, color, Author: Author));
                break;

            case AnnotationTool.Rectangle or AnnotationTool.Ellipse when !isClick:
                var kind = _currentTool == AnnotationTool.Rectangle ? PdfAnnotationKind.Square : PdfAnnotationKind.Circle;
                await AddAnnotationAsync(page, new PdfShapeSpec(kind, Normalize(gesture.Start, point), _strokeWidth, color, Author: Author));
                break;

            case AnnotationTool.Arrow or AnnotationTool.Line when !isClick:
                await AddAnnotationAsync(page, new PdfArrowSpec(gesture.Start, Constrain(page, gesture.Start, point), _strokeWidth, color, Author: Author, Head: _currentTool == AnnotationTool.Arrow));
                break;

            case AnnotationTool.Redact when !isClick:
                EndRedactionGesture(page, gesture.Start, point);
                break;

            case AnnotationTool.MeasureDistance or AnnotationTool.MeasurePerimeter or AnnotationTool.MeasureArea or AnnotationTool.Calibrate:
                await EndMeasureGestureAsync(page, gesture.Start, point, isClick);
                break;

            case AnnotationTool.Count when isClick:
                CountAt(page, gesture.Start);
                break;

            case var tool when IsFieldTool(tool):
                EndFieldGesture(page, gesture.Start, point, isClick);
                break;

            case AnnotationTool.FieldEdit when isClick:
                await EditFieldAtAsync(page, gesture.Start);
                break;

            case AnnotationTool.EditText when isClick:
                await BeginTextEditAsync(page, point);
                break;
        }
    }

    // --- Text selection actions ---------------------------------------------------------------

    public async void CopySelection()
    {
        if (_textSelection is { Text.Length: > 0 } selection && await EnsureAllowedAsync(Core.Pdf.Security.PdfPermissions.Copy, "copy its text"))
        {
            try
            {
                Clipboard.SetText(selection.Text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
                StatusMessage = "Copied";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                StatusMessage = "The clipboard is busy; try again.";
            }
        }
    }

    public async Task MarkupSelectionAsync(PdfAnnotationKind kind)
    {
        if (_textSelection is not { } selection || _textSelectionPage is not { } page)
        {
            return;
        }

        var color = kind == PdfAnnotationKind.Highlight ? _annotationColor : kind == PdfAnnotationKind.StrikeOut ? Palette[4] : Color.FromRgb(0x2E, 0x7D, 0x32);
        ClearTextSelection();
        await AddAnnotationAsync(page, new PdfTextMarkupSpec(kind, MergeByLine(selection.Rects), new PdfColor(color.R, color.G, color.B), Author: Author));
    }

    public void ClearTextSelection()
    {
        if (_textSelection is null)
        {
            return;
        }

        _textSelectionPage?.ClearPreview();
        _textSelection = null;
        _textSelectionPage = null;
        OnPropertyChanged(nameof(HasTextSelection));
    }

    // --- Comments -----------------------------------------------------------------------------

    /// <summary>Saves an edited comment text (called when its editor loses focus).</summary>
    public async Task SaveCommentAsync(CommentViewModel comment)
    {
        if (_document is not { } document || !comment.IsModified)
        {
            return;
        }

        var text = comment.EditText;
        await RunAnnotationChangeAsync(comment.PageIndex, () => document.SetAnnotationContentsAsync(comment.Reference, text), keepSelectionId: comment.Annotation.Id);
    }

    public async Task DeleteCommentAsync(CommentViewModel comment)
    {
        if (_document is not { } document)
        {
            return;
        }

        if (SelectedComment == comment)
        {
            SelectedComment = null;
        }

        // Hidden rather than removed until Save, so Undo brings back exactly what the file had.
        var reference = comment.Reference;
        await RunAnnotationChangeAsync(reference.PageIndex, () => document.DeleteAnnotationAsync(reference));
        _undo.Push(
            () => _ = RunAnnotationChangeAsync(reference.PageIndex, () => document.RestoreAnnotationAsync(reference), keepSelectionId: reference.Id),
            () => _ = RunAnnotationChangeAsync(reference.PageIndex, () => document.DeleteAnnotationAsync(reference)));
        StatusMessage = "Comment deleted. Undo (Ctrl+Z) brings it back.";
    }

    /// <summary>
    /// Before saving: deleted comments leave the document for good. Field looks find their widgets by place
    /// in the page's annotations, so they move up past the removed ones.
    /// </summary>
    private async Task<List<PdfFieldLook>> PurgeDeletedCommentsAsync(IPdfDocument document, List<PdfFieldLook> looks)
    {
        var removed = await document.PurgeDeletedAnnotationsAsync();
        if (removed.Count == 0)
        {
            return looks;
        }

        // Steps that name annotations by place no longer fit (if saving fails, the document stays open).
        _undo.KeepOnlyPageSteps();
        return looks
            .Select(look => look with { AnnotIndex = look.AnnotIndex - removed.Count(r => r.PageIndex == look.PageIndex && r.Index < look.AnnotIndex) })
            .ToList();
    }

    private void SelectComment(CommentViewModel? comment) => SelectedComment = comment;

    private CommentViewModel? HitTestComment(PageViewModel page, NormalizedPoint point) =>
        Comments
            .Where(c => c.PageIndex == page.Index && Contains(c.Annotation.Bounds, point))
            .OrderBy(c => c.Annotation.Bounds.Width * c.Annotation.Bounds.Height) // the smallest is the most specific
            .FirstOrDefault();

    private async Task LoadCommentsAsync(IPdfDocument document)
    {
        var generation = ++_commentsGeneration;
        SelectedComment = null;
        Comments.Clear();
        OnPropertyChanged(nameof(HasComments));
        for (var page = 0; page < document.PageCount; page++)
        {
            IReadOnlyList<PdfAnnotation> annotations;
            try
            {
                annotations = await document.GetAnnotationsAsync(page);
            }
            catch (Exception ex) when (ex is PdfException or ObjectDisposedException or OperationCanceledException)
            {
                continue;
            }

            if (generation != _commentsGeneration || document != _document)
            {
                return; // A newer load started, or the document closed.
            }

            foreach (var annotation in annotations.Where(a => a.IsComment))
            {
                Comments.Add(new CommentViewModel(annotation));
            }
        }

        OnPropertyChanged(nameof(HasComments));
    }

    private async Task ReloadCommentsForPageAsync(IPdfDocument document, int pageIndex, string? keepSelectionId)
    {
        var annotations = await document.GetAnnotationsAsync(pageIndex);
        if (document != _document)
        {
            return;
        }

        if (SelectedComment?.PageIndex == pageIndex)
        {
            SelectedComment = null;
        }

        foreach (var old in Comments.Where(c => c.PageIndex == pageIndex).ToList())
        {
            Comments.Remove(old);
        }

        var insertAt = Comments.TakeWhile(c => c.PageIndex < pageIndex).Count();
        foreach (var annotation in annotations.Where(a => a.IsComment))
        {
            var comment = new CommentViewModel(annotation);
            Comments.Insert(insertAt++, comment);
            if (keepSelectionId is { Length: > 0 } && annotation.Id == keepSelectionId)
            {
                SelectedComment = comment;
            }
        }

        OnPropertyChanged(nameof(HasComments));
    }

    // --- Creating and changing annotations ----------------------------------------------------

    private string Author => Environment.UserName;

    /// <summary>Adds an annotation with undo/redo. Returns its id, or null if it failed.</summary>
    private async Task<string?> AddAnnotationAsync(PageViewModel page, PdfAnnotationSpec spec)
    {
        if (_document is not { } document)
        {
            return null;
        }

        string? id = null;
        var pageIndex = page.Index;
        await RunAnnotationChangeAsync(pageIndex, async () => id = await document.AddAnnotationAsync(pageIndex, spec));
        if (id is null)
        {
            return null;
        }

        // Redo creates the annotation again with a new id, so the undo always targets the current one.
        var current = id;
        _undo.Push(
            () => _ = RunAnnotationChangeAsync(pageIndex, () => document.RemoveAnnotationAsync(new PdfAnnotationRef(pageIndex, -1, current))),
            () => _ = RunAnnotationChangeAsync(pageIndex, async () => current = await document.AddAnnotationAsync(pageIndex, spec)));
        return id;
    }

    /// <summary>Runs an annotation change, then redraws the page and refreshes its comments.</summary>
    private async Task RunAnnotationChangeAsync(int pageIndex, Func<Task> change, string? keepSelectionId = null)
    {
        if (_document is not { } document)
        {
            return;
        }

        try
        {
            await change();
            _documentModified = true;
            OnEditsChanged();
        }
        catch (Exception ex) when (ex is PdfException or ArgumentException)
        {
            _dialogs.ShowError(ex.Message);
        }
        catch (ObjectDisposedException)
        {
            return; // The document closed meanwhile.
        }

        if (document != _document || pageIndex >= Pages.Count)
        {
            return;
        }

        var page = Pages[pageIndex];
        page.ReleaseImage();
        ReleaseThumbnail(page);
        RequestThumbnail(page);
        UpdateVisiblePages();
        try
        {
            await ReloadCommentsForPageAsync(document, pageIndex, keepSelectionId);
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
        {
        }
    }

    // --- Selection preview --------------------------------------------------------------------

    /// <summary>Asks the engine for the selection under the drag, one request at a time.</summary>
    private async Task RequestSelectionPreviewAsync(Gesture gesture)
    {
        if (_selectionRequestRunning || _document is not { } document)
        {
            return;
        }

        _selectionRequestRunning = true;
        try
        {
            NormalizedPoint requested;
            do
            {
                requested = gesture.Latest;
                var selection = await SafeSelectAsync(document, gesture.Page, gesture.Start, requested);
                if (_gesture != gesture)
                {
                    return; // The drag ended meanwhile.
                }

                var fill = _currentTool == AnnotationTool.Select
                    ? SelectionFill
                    : Frozen(new SolidColorBrush(Color.FromArgb(0x66, _annotationColor.R, _annotationColor.G, _annotationColor.B)));
                gesture.Page.SetPreview(selection is null ? null : RectsGeometry(gesture.Page, MergeByLine(selection.Rects)), fill, null, 0);
            }
            while (gesture.Latest != requested); // The mouse moved while we waited: catch up.
        }
        finally
        {
            _selectionRequestRunning = false;
        }
    }

    private static async Task<PdfTextSelection?> SafeSelectAsync(IPdfDocument document, PageViewModel page, NormalizedPoint start, NormalizedPoint end)
    {
        try
        {
            return await document.GetTextSelectionAsync(page.Index, start, end);
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException or ArgumentException)
        {
            return null;
        }
    }

    // --- Geometry helpers ---------------------------------------------------------------------

    /// <summary>
    /// Joins the per-character rectangles of a selection into one rectangle per line, so highlights
    /// are clean bands (some PDFs position every glyph separately).
    /// </summary>
    internal static List<NormalizedRect> MergeByLine(IReadOnlyList<NormalizedRect> rects)
    {
        var lines = new List<NormalizedRect>();
        foreach (var rect in rects.Where(r => r.Width > 0 && r.Height > 0))
        {
            var index = lines.FindIndex(line =>
            {
                var overlap = Math.Min(line.Y + line.Height, rect.Y + rect.Height) - Math.Max(line.Y, rect.Y);
                var gap = Math.Max(line.X, rect.X) - Math.Min(line.X + line.Width, rect.X + rect.Width);
                return overlap > Math.Min(line.Height, rect.Height) * 0.5 && gap < rect.Height * 1.5;
            });
            if (index < 0)
            {
                lines.Add(rect);
                continue;
            }

            var l = lines[index];
            var left = Math.Min(l.X, rect.X);
            var top = Math.Min(l.Y, rect.Y);
            lines[index] = new NormalizedRect(left, top, Math.Max(l.X + l.Width, rect.X + rect.Width) - left, Math.Max(l.Y + l.Height, rect.Y + rect.Height) - top);
        }

        return lines;
    }

    private static PdfAnnotationKind MarkupKind(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Underline => PdfAnnotationKind.Underline,
        AnnotationTool.StrikeOut => PdfAnnotationKind.StrikeOut,
        _ => PdfAnnotationKind.Highlight,
    };

    /// <summary>Drops points closer than half a point to the previous one; pen strokes stay light.</summary>
    private static List<NormalizedPoint> Simplify(PageViewModel page, IReadOnlyList<NormalizedPoint> points)
    {
        var result = new List<NormalizedPoint> { points[0] };
        foreach (var point in points.Skip(1))
        {
            if (Distance(page, result[^1], point) >= 0.5)
            {
                result.Add(point);
            }
        }

        if (result.Count == 1)
        {
            result.Add(points[^1]);
        }

        return result;
    }

    private static NormalizedRect Normalize(NormalizedPoint a, NormalizedPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static bool Contains(NormalizedRect rect, NormalizedPoint point) =>
        point.X >= rect.X && point.X <= rect.X + rect.Width && point.Y >= rect.Y && point.Y <= rect.Y + rect.Height;

    private static double Distance(PageViewModel page, NormalizedPoint a, NormalizedPoint b) =>
        Math.Sqrt(Math.Pow((a.X - b.X) * page.WidthPoints, 2) + Math.Pow((a.Y - b.Y) * page.HeightPoints, 2));

    private static Point ToPoint(PageViewModel page, NormalizedPoint p) => new(p.X * page.WidthPoints, p.Y * page.HeightPoints);

    private static Rect ToPoints(PageViewModel page, NormalizedPoint a, NormalizedPoint b) => new(ToPoint(page, a), ToPoint(page, b));

    private static Geometry Polyline(PageViewModel page, IReadOnlyList<NormalizedPoint> points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(ToPoint(page, points[0]), isFilled: false, isClosed: false);
            context.PolyLineTo(points.Skip(1).Select(p => ToPoint(page, p)).ToList(), isStroked: true, isSmoothJoin: true);
        }

        return geometry;
    }

    private static Geometry RectsGeometry(PageViewModel page, IEnumerable<NormalizedRect> rects)
    {
        var group = new GeometryGroup();
        foreach (var r in rects)
        {
            group.Children.Add(new RectangleGeometry(new Rect(r.X * page.WidthPoints, r.Y * page.HeightPoints, r.Width * page.WidthPoints, r.Height * page.HeightPoints)));
        }

        return group;
    }

    /// <summary>The same arrow shape the engine stores: a line plus a 25° "V" at the end.</summary>
    private static Geometry ArrowGeometry(Point start, Point end, double width)
    {
        var dx = start.X - end.X;
        var dy = start.Y - end.Y;
        var length = Math.Max(Math.Sqrt((dx * dx) + (dy * dy)), 0.001);
        var head = Math.Max(8, width * 4);
        (dx, dy) = (dx / length * head, dy / length * head);
        var angle = 25 * Math.PI / 180;
        Point Rotate(double a) => new(end.X + (dx * Math.Cos(a)) - (dy * Math.Sin(a)), end.Y + (dx * Math.Sin(a)) + (dy * Math.Cos(a)));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.LineTo(end, true, true);
            context.BeginFigure(Rotate(angle), false, false);
            context.LineTo(end, true, true);
            context.LineTo(Rotate(-angle), true, true);
        }

        return geometry;
    }

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private sealed class Gesture(PageViewModel page, NormalizedPoint start, List<NormalizedPoint> points)
    {
        public PageViewModel Page { get; } = page;

        public NormalizedPoint Start { get; } = start;

        /// <summary>Where the mouse went down, when <see cref="Start"/> was snapped to the drawing.</summary>
        public NormalizedPoint? Pressed { get; init; }

        public List<NormalizedPoint> Points { get; } = points;

        public NormalizedPoint Latest { get; set; } = start;
    }
}
