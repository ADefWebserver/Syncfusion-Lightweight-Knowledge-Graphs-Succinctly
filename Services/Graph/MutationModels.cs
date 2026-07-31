namespace SyncfusionHelpDesk.Services.Graph;

public enum MutationStatus { Rejected, PreviewOnly, Applied }

public sealed record MutationPreview(
    string Summary,
    IReadOnlyList<string> AffectedFiles,
    IReadOnlyList<string> Warnings);

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
