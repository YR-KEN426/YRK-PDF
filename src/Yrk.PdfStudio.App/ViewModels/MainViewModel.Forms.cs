using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Forms;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// Creating forms (Fill &amp; Sign tab): drag a box (or click) with a field tool to add a text box, check box,
/// dropdown or signature field. The new fields are written into the PDF when it is saved, then work like
/// any form: fill them here or in other PDF programs.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly List<FormFieldDraftViewModel> _newFields = [];
    private IRelayCommand<FormFieldDraftViewModel>? _removeNewFieldCommand;

    public bool HasNewFields => _newFields.Count > 0;

    public IRelayCommand<FormFieldDraftViewModel> RemoveNewFieldCommand => _removeNewFieldCommand ??= new RelayCommand<FormFieldDraftViewModel>(draft =>
    {
        if (draft is not null)
        {
            RemoveNewField(draft);
            _undo.Push(() => AddNewField(draft), () => RemoveNewField(draft));
        }
    });

    private static bool IsFieldTool(AnnotationTool tool) =>
        tool is AnnotationTool.FieldText or AnnotationTool.FieldMultiline or AnnotationTool.FieldCheckBox or AnnotationTool.FieldDropdown or AnnotationTool.FieldSignature;

    private static PdfNewFieldKind FieldKind(AnnotationTool tool) => tool switch
    {
        AnnotationTool.FieldMultiline => PdfNewFieldKind.MultilineText,
        AnnotationTool.FieldCheckBox => PdfNewFieldKind.CheckBox,
        AnnotationTool.FieldDropdown => PdfNewFieldKind.Dropdown,
        AnnotationTool.FieldSignature => PdfNewFieldKind.Signature,
        _ => PdfNewFieldKind.Text,
    };

    /// <summary>The size a field gets from a click (points).</summary>
    private static (double Width, double Height) DefaultFieldSize(PdfNewFieldKind kind) => kind switch
    {
        PdfNewFieldKind.MultilineText => (260, 60),
        PdfNewFieldKind.CheckBox => (14, 14),
        PdfNewFieldKind.Dropdown => (160, 20),
        PdfNewFieldKind.Signature => (180, 50),
        _ => (180, 20),
    };

    /// <summary>A drag (or a click, for the default size) with a field tool: ask for the name, then place it.</summary>
    private void EndFieldGesture(PageViewModel page, NormalizedPoint start, NormalizedPoint end, bool isClick)
    {
        page.ClearPreview();
        if (_document?.IsEncrypted == true)
        {
            _dialogs.ShowInfo("Remove the password protection first (Tools › Remove password protection), then add form fields.");
            return;
        }

        var kind = FieldKind(CurrentTool);
        var (defaultWidth, defaultHeight) = DefaultFieldSize(kind);
        var box = Normalize(start, end);
        var width = box.Width * page.WidthPoints;
        var height = box.Height * page.HeightPoints;
        if (isClick || width < 6 || height < 6)
        {
            box = new NormalizedRect(start.X, start.Y, defaultWidth / page.WidthPoints, defaultHeight / page.HeightPoints);
        }
        else if (kind == PdfNewFieldKind.CheckBox)
        {
            var side = Math.Min(width, height); // Check boxes stay square.
            box = box with { Width = side / page.WidthPoints, Height = side / page.HeightPoints };
        }

        box = box with { X = Math.Clamp(box.X, 0, 1 - box.Width), Y = Math.Clamp(box.Y, 0, 1 - box.Height) };
        var count = _newFields.Count(f => f.Field.Kind == kind) + 1;
        if (_dialogs.AskFormField(kind, $"{Loc.T(DefaultFieldName(kind))} {count}") is not { } answer)
        {
            return;
        }

        var draft = new FormFieldDraftViewModel(page, new PdfNewField(page.Index, box, kind, answer.Name, answer.Options, answer.Required));
        AddNewField(draft);
        _undo.Push(() => RemoveNewField(draft), () => AddNewField(draft));
        StatusMessage = "Field added. It becomes part of the document when you save.";
    }

    private static string DefaultFieldName(PdfNewFieldKind kind) => kind switch
    {
        PdfNewFieldKind.MultilineText => "Long text",
        PdfNewFieldKind.CheckBox => "Check box",
        PdfNewFieldKind.Dropdown => "Dropdown",
        PdfNewFieldKind.Signature => "Signature",
        _ => "Text",
    };

    private void AddNewField(FormFieldDraftViewModel draft)
    {
        if (!Pages.Contains(draft.Page) || _newFields.Contains(draft))
        {
            return;
        }

        _newFields.Add(draft);
        draft.Page.NewFields.Add(draft);
        OnPropertyChanged(nameof(HasNewFields));
        OnEditsChanged();
    }

    private void RemoveNewField(FormFieldDraftViewModel draft)
    {
        _newFields.Remove(draft);
        draft.Page.NewFields.Remove(draft);
        OnPropertyChanged(nameof(HasNewFields));
        OnEditsChanged();
    }

    private void ClearNewFields()
    {
        foreach (var draft in _newFields.ToList())
        {
            draft.Page.NewFields.Remove(draft);
        }

        _newFields.Clear();
        OnPropertyChanged(nameof(HasNewFields));
        foreach (var change in _fieldChanges.Values.ToList())
        {
            change.Page.ChangedFields.Remove(change);
        }

        _fieldChanges.Clear();
    }

    /// <summary>Writes field changes, then the new fields, into the saved bytes (incremental updates).</summary>
    private byte[] ApplyNewFields(byte[] pdf)
    {
        if (_fieldChanges.Count > 0)
        {
            pdf = PdfFormBuilder.ChangeFields(pdf, _fieldChanges.Values.Select(c => c.Change).ToList());
        }

        return _newFields.Count == 0 ? pdf : PdfFormBuilder.AddFields(pdf, _newFields.Select(f => f.Field).ToList());
    }

    // --- Changing fields (the "Edit fields" tool) --------------------------------------------------

    /// <summary>Changes to fields already in the document, by full name, waiting for Save.</summary>
    private readonly Dictionary<string, FieldChangeViewModel> _fieldChanges = new(StringComparer.Ordinal);

    public bool IsEditingFields => CurrentTool == AnnotationTool.FieldEdit;

    /// <summary>A click with the Edit fields tool: change or delete the field there (a new one or a saved one).</summary>
    private async Task EditFieldAtAsync(PageViewModel page, NormalizedPoint point)
    {
        static bool Inside(NormalizedRect r, NormalizedPoint p) =>
            p.X >= r.X && p.X <= r.X + r.Width && p.Y >= r.Y && p.Y <= r.Y + r.Height;

        // A field added in this session (not saved yet).
        if (page.NewFields.LastOrDefault(d => Inside(d.Field.Bounds, point)) is { } draft)
        {
            var field = draft.Field;
            if (_dialogs.AskFormField(field.Kind, field.Name, new FormFieldAnswer(field.Name, field.Options, field.Required)) is not { } edited)
            {
                return;
            }

            RemoveNewField(draft);
            if (!edited.Delete)
            {
                AddNewField(new FormFieldDraftViewModel(page, field with { Name = edited.Name, Options = edited.Options ?? field.Options, Required = edited.Required }));
            }

            return;
        }

        if (_document is not { } document)
        {
            return;
        }

        if (document.IsEncrypted)
        {
            _dialogs.ShowInfo("Remove the password protection first (Tools › Remove password protection), then change form fields.");
            return;
        }

        IReadOnlyList<PdfFormField> fields;
        try
        {
            fields = document.HasForm ? await document.GetFormFieldsAsync(page.Index) : [];
        }
        catch (PdfException)
        {
            fields = [];
        }

        var hit = fields.Where(f => Inside(f.Bounds, point)).OrderBy(f => f.Bounds.Width * f.Bounds.Height).FirstOrDefault();
        if (hit is null)
        {
            StatusMessage = "Click a form field to change or delete it";
            return;
        }

        var kind = hit.Type switch
        {
            PdfFormFieldType.CheckBox or PdfFormFieldType.RadioButton or PdfFormFieldType.PushButton => PdfNewFieldKind.CheckBox,
            PdfFormFieldType.ComboBox or PdfFormFieldType.ListBox => PdfNewFieldKind.Dropdown,
            PdfFormFieldType.Signature => PdfNewFieldKind.Signature,
            _ => hit.IsMultiline ? PdfNewFieldKind.MultilineText : PdfNewFieldKind.Text,
        };
        _fieldChanges.TryGetValue(hit.Name, out var pending);
        var shownName = pending?.Change.NewName ?? hit.Name.Split('.')[^1];
        var current = new FormFieldAnswer(shownName, pending?.Change.Options ?? hit.Options, pending?.Change.Required ?? hit.IsRequired);
        if (_dialogs.AskFormField(kind, shownName, current) is not { } answer)
        {
            return;
        }

        var change = answer.Delete
            ? new PdfFieldChange(hit.Name, Delete: true)
            : new PdfFieldChange(
                hit.Name,
                NewName: answer.Name != hit.Name.Split('.')[^1] ? answer.Name : null,
                Options: kind == PdfNewFieldKind.Dropdown ? answer.Options : null,
                Required: answer.Required != hit.IsRequired ? answer.Required : null);
        if (pending is not null)
        {
            page.ChangedFields.Remove(pending);
            _fieldChanges.Remove(hit.Name);
        }

        if (change.Delete || change.NewName is not null || change.Options is not null || change.Required is not null)
        {
            var view = new FieldChangeViewModel(page, hit.Bounds, change);
            _fieldChanges[hit.Name] = view;
            page.ChangedFields.Add(view);
            StatusMessage = change.Delete ? "The field will be deleted when you save." : "The field will be changed when you save.";
        }

        OnEditsChanged();
    }
}
