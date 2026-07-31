using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyncfusionHelpDesk.Data;
using SyncfusionHelpDesk.Graph;
using SyncfusionHelpDesk.Models;

namespace SyncfusionHelpDesk.Services.Graph;

/// <summary>
/// The only component allowed to write graph files. Every mutation follows the
/// same two-phase contract: <c>confirmed == false</c> validates and previews
/// without touching SQL or disk; <c>confirmed == true</c> applies the change,
/// rebuilds the in-memory indexes, updates the manifest, and appends an audit
/// entry. All work happens on a <see cref="GraphDocument"/> freshly loaded from
/// disk, so a rejected or preview-only call can never leave the in-memory graph
/// half-changed.
/// </summary>
public sealed class GraphMutationService
{
    private const string AuditFileName = "audit.log";

    // Static so writes serialize process-wide even though the service is scoped
    // and a new instance exists per request.
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    private static readonly HashSet<string> ReadOnlyNodeTypes = new(StringComparer.Ordinal)
    {
        "Ticket", "TicketDetail", "Requester", "Status", "Day",
    };

    private static readonly HashSet<string> EditableTicketProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "description",
    };

    private static readonly string[] PreviewFiles =
        { GraphFile.GraphFileName, GraphFile.ManifestFileName, AuditFileName };

    private readonly GraphStore _store;
    private readonly GraphOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly IDbContextFactory<SyncfusionHelpDeskContext> _contextFactory;

    public GraphMutationService(
        GraphStore store,
        IOptions<GraphOptions> options,
        IWebHostEnvironment environment,
        IDbContextFactory<SyncfusionHelpDeskContext> contextFactory)
    {
        _store = store;
        _options = options.Value;
        _environment = environment;
        _contextFactory = contextFactory;
    }

    // ----- UpdateTicket (SQL system of record, then mirror) -----

    public async Task<MutationResult> UpdateTicket(
        string ticketId, string propertyName, string value,
        bool confirmed = false, CancellationToken ct = default)
    {
        var isStatus = string.Equals(propertyName, "status", StringComparison.OrdinalIgnoreCase);
        var isDescription = string.Equals(propertyName, "description", StringComparison.OrdinalIgnoreCase);

        string? canonicalStatus = null;
        var ticketKey = 0;

        MutationResult? Validate(GraphDocument doc)
        {
            var node = doc.Nodes.FirstOrDefault(n => n.Id == ticketId);
            if (node is null)
            {
                return MutationResult.Rejected($"No node found with id '{ticketId}'.");
            }

            if (!string.Equals(node.Type, "Ticket", StringComparison.Ordinal))
            {
                return MutationResult.Rejected(
                    $"Node '{ticketId}' is a {node.Type}, not a Ticket. Only Ticket nodes can be updated with UpdateTicket.");
            }

            if (!EditableTicketProperties.Contains(propertyName))
            {
                return MutationResult.Rejected(
                    $"Property '{propertyName}' cannot be edited. Only 'status' or 'description' may be changed on a Ticket.");
            }

            if (isStatus)
            {
                var match = HelpDeskStatusData.Statuses
                    .FirstOrDefault(s => string.Equals(s.ID, value, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    var valid = string.Join(", ", HelpDeskStatusData.Statuses.Select(s => s.ID));
                    return MutationResult.Rejected($"'{value}' is not a valid status. Valid statuses are: {valid}.");
                }

                canonicalStatus = match.ID;
            }

            if (isDescription && string.IsNullOrWhiteSpace(value))
            {
                return MutationResult.Rejected("A ticket description must not be blank.");
            }

            if (!TryParseTicketKey(ticketId, out ticketKey))
            {
                return MutationResult.Rejected(
                    $"Ticket id '{ticketId}' is not in the form 'ticket:<number>'.");
            }

            return null;
        }

        var document = await LoadDocumentAsync(ct);
        if (Validate(document) is { } rejection)
        {
            return rejection;
        }

        var summary = isStatus
            ? $"Change ticket {ticketId} status to '{canonicalStatus}'. Writes to the SQL database, then mirrors into the graph."
            : $"Change ticket {ticketId} description. Writes to the SQL database, then mirrors into the graph.";
        var preview = new MutationPreview(summary, PreviewFiles, Array.Empty<string>());

        if (!confirmed)
        {
            return MutationResult.PreviewOnly(preview);
        }

        await WriteGate.WaitAsync(ct);
        try
        {
            document = await LoadDocumentAsync(ct);
            if (Validate(document) is { } lateRejection)
            {
                return lateRejection;
            }

            var node = document.Nodes.First(n => n.Id == ticketId);

            await using var context = await _contextFactory.CreateDbContextAsync(ct);
            var ticket = await context.HelpDeskTickets
                .FirstOrDefaultAsync(t => t.Id == ticketKey, ct);
            if (ticket is null)
            {
                return MutationResult.Rejected(
                    $"Ticket {ticketKey} no longer exists in the database.");
            }

            if (isStatus)
            {
                ticket.TicketStatus = canonicalStatus!;
                await context.SaveChangesAsync(ct);

                node.Data["status"] = canonicalStatus;
                RepointStatusEdge(document, ticketId, canonicalStatus!);
            }
            else
            {
                ticket.TicketDescription = value;
                await context.SaveChangesAsync(ct);

                node.Label = Truncate(value, 60);
            }

            await CommitAsync(document, $"UpdateTicket id={ticketId} property={propertyName}", ct);
            return MutationResult.Applied(preview);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    // ----- UpdateNodeContent (knowledge layer only) -----

    public async Task<MutationResult> UpdateNodeContent(
        string nodeId, string propertyName, string value,
        bool confirmed = false, CancellationToken ct = default)
    {
        var isLabel = string.Equals(propertyName, "label", StringComparison.OrdinalIgnoreCase);

        MutationResult? Validate(GraphDocument doc)
        {
            var node = doc.Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node is null)
            {
                return MutationResult.Rejected($"No node found with id '{nodeId}'.");
            }

            if (ReadOnlyNodeTypes.Contains(node.Type))
            {
                return MutationResult.Rejected(
                    $"Node '{nodeId}' is a read-only {node.Type} node derived from the database and cannot be edited.");
            }

            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return MutationResult.Rejected("A property name is required.");
            }

            return null;
        }

        var document = await LoadDocumentAsync(ct);
        if (Validate(document) is { } rejection)
        {
            return rejection;
        }

        var target = document.Nodes.First(n => n.Id == nodeId);
        var summary = isLabel
            ? $"Change the label of {target.Type} node '{nodeId}'."
            : $"Update the '{propertyName}' property of {target.Type} node '{nodeId}'.";
        var preview = new MutationPreview(summary, PreviewFiles, Array.Empty<string>());

        if (!confirmed)
        {
            return MutationResult.PreviewOnly(preview);
        }

        await WriteGate.WaitAsync(ct);
        try
        {
            document = await LoadDocumentAsync(ct);
            if (Validate(document) is { } lateRejection)
            {
                return lateRejection;
            }

            var node = document.Nodes.First(n => n.Id == nodeId);
            if (isLabel)
            {
                node.Label = value;
            }
            else
            {
                node.Data[propertyName] = value;
            }

            await CommitAsync(document, $"UpdateNodeContent id={nodeId} property={propertyName}", ct);
            return MutationResult.Applied(preview);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    // ----- LinkTickets -----

    public async Task<MutationResult> LinkTickets(
        string fromTicketId, string toTicketId,
        bool confirmed = false, CancellationToken ct = default)
    {
        MutationResult? Validate(GraphDocument doc)
        {
            if (string.Equals(fromTicketId, toTicketId, StringComparison.Ordinal))
            {
                return MutationResult.Rejected("A ticket cannot be linked to itself.");
            }

            if (!IsNodeOfType(doc, fromTicketId, "Ticket"))
            {
                return MutationResult.Rejected($"'{fromTicketId}' is not an existing Ticket node.");
            }

            if (!IsNodeOfType(doc, toTicketId, "Ticket"))
            {
                return MutationResult.Rejected($"'{toTicketId}' is not an existing Ticket node.");
            }

            var duplicate = doc.Edges.Any(e => e.Type == "LINKED_TO" &&
                ((e.From == fromTicketId && e.To == toTicketId) ||
                 (e.From == toTicketId && e.To == fromTicketId)));
            if (duplicate)
            {
                return MutationResult.Rejected(
                    $"A LINKED_TO edge already connects {fromTicketId} and {toTicketId}.");
            }

            return null;
        }

        var document = await LoadDocumentAsync(ct);
        if (Validate(document) is { } rejection)
        {
            return rejection;
        }

        var preview = new MutationPreview(
            $"Link ticket {fromTicketId} to {toTicketId} with a LINKED_TO edge.",
            PreviewFiles, Array.Empty<string>());

        if (!confirmed)
        {
            return MutationResult.PreviewOnly(preview);
        }

        await WriteGate.WaitAsync(ct);
        try
        {
            document = await LoadDocumentAsync(ct);
            if (Validate(document) is { } lateRejection)
            {
                return lateRejection;
            }

            document.Edges.Add(new GraphEdge
            {
                From = fromTicketId,
                To = toTicketId,
                Type = "LINKED_TO",
            });

            await CommitAsync(document, $"LinkTickets from={fromTicketId} to={toTicketId}", ct);
            return MutationResult.Applied(preview);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    // ----- AddKnowledgeArticle -----

    public async Task<MutationResult> AddKnowledgeArticle(
        string title, string content,
        bool confirmed = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return MutationResult.Rejected("A knowledge article requires a title.");
        }

        var articleId = $"article:{Guid.NewGuid():N}";
        var preview = new MutationPreview(
            $"Create a KnowledgeArticle node titled '{title}'.",
            PreviewFiles, Array.Empty<string>());

        if (!confirmed)
        {
            return MutationResult.PreviewOnly(preview);
        }

        await WriteGate.WaitAsync(ct);
        try
        {
            var document = await LoadDocumentAsync(ct);
            document.Nodes.Add(new GraphNode
            {
                Id = articleId,
                Type = "KnowledgeArticle",
                Label = Truncate(title, 60),
                Data =
                {
                    ["title"] = title,
                    ["content"] = content,
                },
            });

            await CommitAsync(document, $"AddKnowledgeArticle id={articleId}", ct);
            return MutationResult.Applied(preview);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    // ----- ReferenceArticle -----

    public async Task<MutationResult> ReferenceArticle(
        string ticketId, string articleId,
        bool confirmed = false, CancellationToken ct = default)
    {
        MutationResult? Validate(GraphDocument doc)
        {
            if (!IsNodeOfType(doc, ticketId, "Ticket"))
            {
                return MutationResult.Rejected($"'{ticketId}' is not an existing Ticket node.");
            }

            if (!IsNodeOfType(doc, articleId, "KnowledgeArticle"))
            {
                return MutationResult.Rejected($"'{articleId}' is not an existing KnowledgeArticle node.");
            }

            var duplicate = doc.Edges.Any(e => e.Type == "REFERENCES_ARTICLE" &&
                e.From == ticketId && e.To == articleId);
            if (duplicate)
            {
                return MutationResult.Rejected(
                    $"A REFERENCES_ARTICLE edge already connects {ticketId} to {articleId}.");
            }

            return null;
        }

        var document = await LoadDocumentAsync(ct);
        if (Validate(document) is { } rejection)
        {
            return rejection;
        }

        var preview = new MutationPreview(
            $"Add a REFERENCES_ARTICLE edge from {ticketId} to {articleId}.",
            PreviewFiles, Array.Empty<string>());

        if (!confirmed)
        {
            return MutationResult.PreviewOnly(preview);
        }

        await WriteGate.WaitAsync(ct);
        try
        {
            document = await LoadDocumentAsync(ct);
            if (Validate(document) is { } lateRejection)
            {
                return lateRejection;
            }

            document.Edges.Add(new GraphEdge
            {
                From = ticketId,
                To = articleId,
                Type = "REFERENCES_ARTICLE",
            });

            await CommitAsync(document, $"ReferenceArticle ticket={ticketId} article={articleId}", ct);
            return MutationResult.Applied(preview);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    // ----- RecordResolution -----

    public async Task<MutationResult> RecordResolution(
        string ticketId, string content,
        bool confirmed = false, CancellationToken ct = default)
    {
        MutationResult? Validate(GraphDocument doc)
        {
            if (!IsNodeOfType(doc, ticketId, "Ticket"))
            {
                return MutationResult.Rejected($"'{ticketId}' is not an existing Ticket node.");
            }

            return null;
        }

        var document = await LoadDocumentAsync(ct);
        if (Validate(document) is { } rejection)
        {
            return rejection;
        }

        var resolutionId = $"resolution:{Guid.NewGuid():N}";
        var preview = new MutationPreview(
            $"Create a Resolution node for {ticketId} and connect it with a RESOLVED_BY edge.",
            PreviewFiles, Array.Empty<string>());

        if (!confirmed)
        {
            return MutationResult.PreviewOnly(preview);
        }

        await WriteGate.WaitAsync(ct);
        try
        {
            document = await LoadDocumentAsync(ct);
            if (Validate(document) is { } lateRejection)
            {
                return lateRejection;
            }

            document.Nodes.Add(new GraphNode
            {
                Id = resolutionId,
                Type = "Resolution",
                Label = Truncate(content, 60),
                Data = { ["content"] = content },
            });

            document.Edges.Add(new GraphEdge
            {
                From = ticketId,
                To = resolutionId,
                Type = "RESOLVED_BY",
            });

            await CommitAsync(document, $"RecordResolution ticket={ticketId} resolution={resolutionId}", ct);
            return MutationResult.Applied(preview);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    // ----- DeleteArticle -----

    public async Task<MutationResult> DeleteArticle(
        string articleId, bool cascade = false,
        bool confirmed = false, CancellationToken ct = default)
    {
        MutationResult? Validate(GraphDocument doc)
        {
            if (!IsNodeOfType(doc, articleId, "KnowledgeArticle"))
            {
                return MutationResult.Rejected($"'{articleId}' is not an existing KnowledgeArticle node.");
            }

            var referencing = doc.Edges.Count(e =>
                e.Type == "REFERENCES_ARTICLE" && e.To == articleId);
            if (referencing > 0 && !cascade)
            {
                return MutationResult.Rejected(
                    $"{referencing} REFERENCES_ARTICLE edge(s) still point to {articleId}. Pass cascade to remove them too.");
            }

            return null;
        }

        var document = await LoadDocumentAsync(ct);
        if (Validate(document) is { } rejection)
        {
            return rejection;
        }

        var warnings = new List<string>();
        var incoming = document.Edges.Count(e => e.To == articleId);
        if (incoming > 0)
        {
            warnings.Add($"{incoming} incoming edge(s) will also be removed.");
        }

        var preview = new MutationPreview(
            $"Delete KnowledgeArticle node '{articleId}' and its incoming edges.",
            PreviewFiles, warnings);

        if (!confirmed)
        {
            return MutationResult.PreviewOnly(preview);
        }

        await WriteGate.WaitAsync(ct);
        try
        {
            document = await LoadDocumentAsync(ct);
            if (Validate(document) is { } lateRejection)
            {
                return lateRejection;
            }

            document.Edges.RemoveAll(e => e.To == articleId || e.From == articleId);
            document.Nodes.RemoveAll(n => n.Id == articleId);

            await CommitAsync(document, $"DeleteArticle id={articleId} cascade={cascade}", ct);
            return MutationResult.Applied(preview);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    // ----- Shared write tail -----

    private async Task CommitAsync(
        GraphDocument document, string auditDetail, CancellationToken ct)
    {
        var directory = GraphFile.ResolveDirectory(
            _options.OutputPath, _environment.ContentRootPath);
        var graphPath = Path.Combine(directory, GraphFile.GraphFileName);
        var mutationUtc = DateTime.UtcNow;

        await GraphFile.SaveAtomicAsync(document, graphPath, ct);
        await GraphFile.WriteManifestAsync(
            document, directory, lastMutationUtc: mutationUtc, cancellationToken: ct);
        await _store.ReloadAsync(ct);

        var line = string.Concat(
            mutationUtc.ToString("o", CultureInfo.InvariantCulture),
            "\t",
            auditDetail,
            Environment.NewLine);
        await File.AppendAllTextAsync(Path.Combine(directory, AuditFileName), line, ct);
    }

    // Moves a ticket's HAS_STATUS edge to the node for the new status,
    // creating the Status node when no ticket currently holds that status.
    private static void RepointStatusEdge(
        GraphDocument document, string ticketId, string newStatus)
    {
        var statusId = $"status:{newStatus.ToLowerInvariant()}";

        if (!document.Nodes.Any(n => n.Id == statusId))
        {
            document.Nodes.Add(new GraphNode
            {
                Id = statusId,
                Type = "Status",
                Label = newStatus,
                Data = { ["status"] = newStatus },
            });
        }

        document.Edges.RemoveAll(e => e.Type == "HAS_STATUS" && e.From == ticketId);

        document.Edges.Add(new GraphEdge
        {
            From = ticketId,
            To = statusId,
            Type = "HAS_STATUS",
        });
    }

    private async Task<GraphDocument> LoadDocumentAsync(CancellationToken ct)
    {
        var path = GraphFile.ResolvePath(_options.OutputPath, _environment.ContentRootPath);
        return await GraphFile.LoadAsync(path, ct);
    }

    private static bool IsNodeOfType(GraphDocument document, string id, string type) =>
        document.Nodes.Any(n => n.Id == id && string.Equals(n.Type, type, StringComparison.Ordinal));

    private static bool TryParseTicketKey(string ticketId, out int key)
    {
        key = 0;
        const string prefix = "ticket:";
        if (string.IsNullOrEmpty(ticketId) || !ticketId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(
            ticketId.AsSpan(prefix.Length),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out key);
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        return text.Length <= max ? text : text[..max];
    }
}
