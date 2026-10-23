namespace Yrk.PdfStudio.App.Services;

public enum BatchAction
{
    PdfA,
    Compress,
    PageMarks,
    Protect,
    Export,
}

/// <summary>The files, the job, and the folder for the results (see Tools ▸ Process many files).</summary>
public sealed record BatchRequest(IReadOnlyList<string> Files, BatchAction Action, string OutputFolder);

/// <summary>What the new-field dialog returns.</summary>
public sealed record FormFieldAnswer(string Name, IReadOnlyList<string>? Options, bool Required, bool Delete = false);
