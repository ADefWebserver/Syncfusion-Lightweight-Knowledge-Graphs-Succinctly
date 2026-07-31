namespace SyncfusionHelpDesk.Services.Graph;

/// <summary>The outcome kind of a mutation attempt.</summary>
public enum MutationStatus
{
    Rejected,
    PreviewOnly,
    Applied,
}

/// <summary>A plain-language description of a mutation the user can review.</summary>
public sealed record MutationPreview(
    string Summary,
    IReadOnlyList<string> AffectedFiles,
    IReadOnlyList<string> Warnings);

/// <summary>The result of a two-phase mutation call.</summary>
public sealed record MutationResult(
    MutationStatus Status,
    MutationPreview? Preview,
    IReadOnlyList<string> Errors)
{
    public static MutationResult Rejected(params string[] errors) =>
        new(MutationStatus.Rejected, null, errors);

    public static MutationResult PreviewOnly(MutationPreview preview) =>
        new(MutationStatus.PreviewOnly, preview, Array.Empty<string>());

    public static MutationResult Applied(MutationPreview preview) =>
        new(MutationStatus.Applied, preview, Array.Empty<string>());
}
