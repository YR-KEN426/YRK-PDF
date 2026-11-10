namespace Yrk.PdfStudio.Core.Pdf;

public enum PdfItemOpacityMode
{
    Group,
    /// <summary>The saved artwork is the 100% baseline; its original opacity cannot be recovered.</summary>
    FadeOnly,
    DeleteOnly,
}

/// <summary>A document-local reference. Query again after document changes or saving.</summary>
public sealed record PdfPageItemRef(Guid DocumentId, int PageIndex, long Revision, int Index);

/// <summary>The caller must warn about invalidating signatures before explicitly acknowledging an edit.</summary>
public sealed record PdfItemEditOptions(bool AcknowledgeDigitalSignatures = false);

/// <summary>Bounds use the displayed page's top-left origin, including page rotation.</summary>
public sealed record PdfPageItem(
    PdfPageItemRef Reference, NormalizedRect Bounds, bool HasYrkMark, YrkMarkKind? Kind,
    string? Group, double Opacity, PdfItemOpacityMode OpacityMode, string? EditError = null);
