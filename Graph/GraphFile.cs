using System.Text.Json;

namespace SyncfusionHelpDesk.Graph;

public static class GraphFile
{
    public const string GraphFileName = "graph.json";
    public const string ManifestFileName = "manifest.json";
    public const string MetadataFileName = "metadata.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ResolveDirectory(string outputPath, string contentRoot) =>
        Path.IsPathRooted(outputPath)
            ? outputPath
            : Path.Combine(contentRoot, outputPath);

    public static string ResolvePath(string outputPath, string contentRoot) =>
        Path.Combine(ResolveDirectory(outputPath, contentRoot), GraphFileName);

    public static async Task<GraphDocument> LoadAsync(
        string fullFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(fullFilePath))
        {
            return new GraphDocument();
        }

        await using var stream = File.OpenRead(fullFilePath);
        var document = await JsonSerializer.DeserializeAsync<GraphDocument>(
            stream, JsonOptions, cancellationToken);

        return document ?? new GraphDocument();
    }

    public static Task SaveAtomicAsync(
        GraphDocument document, string fullFilePath,
        CancellationToken cancellationToken = default) =>
        SaveJsonAtomicAsync(document, fullFilePath, cancellationToken);

    public static async Task SaveJsonAtomicAsync<T>(
        T value, string fullFilePath,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(fullFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = fullFilePath + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
        }

        // File.Move with overwrite is atomic on the same volume, so a reader
        // always sees either the old complete file or the new complete file.
        File.Move(tempPath, fullFilePath, overwrite: true);
    }

    public static Task WriteManifestAsync(
        GraphDocument document, string directory,
        DateTime? lastMutationUtc = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = new
        {
            lastBuildUtc = document.GeneratedUtc,
            lastMutationUtc,
            totalNodes = document.Nodes.Count,
            totalEdges = document.Edges.Count,
            nodeCounts = document.Nodes
                .GroupBy(n => n.Type)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count()),
            edgeCounts = document.Edges
                .GroupBy(e => e.Type)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count()),
        };

        var path = Path.Combine(directory, ManifestFileName);
        return SaveJsonAtomicAsync(manifest, path, cancellationToken);
    }

    public static Task WriteMetadataAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        var metadata = new
        {
            description =
                "Knowledge graph derived from the SyncfusionHelpDesk help-desk " +
                "database. Nodes and edges are generated deterministically from " +
                "help-desk tickets and their details.",
            nodeLabels = new Dictionary<string, string>
            {
                ["Ticket"] = "A help-desk ticket opened by a requester.",
                ["TicketDetail"] = "A comment or detail attached to a ticket.",
                ["Requester"] = "The email address that opened one or more tickets.",
                ["Status"] = "A ticket status value (for example New or Closed).",
                ["Day"] = "A distinct calendar day on which a ticket or detail occurred.",
            },
            edgeLabels = new Dictionary<string, string>
            {
                ["REQUESTED_BY"] = "Connects a Ticket to the Requester who opened it.",
                ["HAS_DETAIL"] = "Connects a Ticket to one of its TicketDetail comments.",
                ["HAS_STATUS"] = "Connects a Ticket to its current Status.",
                ["OCCURRED_ON"] = "Connects a Ticket or TicketDetail to the Day it occurred.",
            },
        };

        var path = Path.Combine(directory, MetadataFileName);
        return SaveJsonAtomicAsync(metadata, path, cancellationToken);
    }
}
