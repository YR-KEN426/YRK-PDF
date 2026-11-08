namespace Yrk.PdfStudio.Core.Pdf;

public enum YrkMarkKind
{
    Image,
    Stamp,
    StampText,
    Signature,
    Text,
}

/// <summary>Invisible provenance for one placed item; all of its objects share the same group.</summary>
public sealed record YrkMark
{
    public YrkMark(YrkMarkKind kind, Guid group)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        Group = group.ToString("N");
    }

    public YrkMarkKind Kind { get; }
    public string Group { get; }
}
