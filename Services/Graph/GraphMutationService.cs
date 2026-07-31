using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyncfusionHelpDesk.Data;
using SyncfusionHelpDesk.Graph;
using SyncfusionHelpDesk.Models;

namespace SyncfusionHelpDesk.Services.Graph;

/// <summary>
/// The only component allowed to write graph files. Every mutation follows the
/// same two-phase shape: a lock-free preview that reads and validates, then an
/// optional confirmed apply that re-validates under a process-wide lock,
/// commits atomically, and appends an audit entry.
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

    // ----- Ticket edit (SQL is the system of record) -----

    public Task<MutationResult> UpdateTicket(
        string ticketId, string propertyName, string value,
        bool confirmed = false, CancellationToken ct = default)
    {
        var isStatus = string.Equals(propertyName, "status", StringComparison.OrdinalIgnoreCase);
        var isDescription = string.Equals(propertyName, "description", StringComparison.OrdinalIgnoreCase);
        string? canonicalStatus = null;

        MutationResult? Validate(GraphDocument doc)
        {
            var node = doc.Nodes.FirstOrDefault(n => n.Id == ticketId);
            if (node is null)
            {
                return MutationResult.Rejected($"No node with id '{ticketId}' exists.");
            }

            if (node.Type != "Ticket")
            {
                return MutationResult.Rejected(
                    $"Node '{ticketId}' is a {node.Type}, not a Ticket, and cannot be edited here.");
            }

            if (!EditableTicketProperties.Contains(propertyName))
            {
                return MutationResult.Rejected(
                    $"'{propertyName}' is not an editable ticket property. " +
                    "Only 'status' and 'description' may change.");
            }

            if (isStatus)
            {
                var match = HelpDeskStatusData.Statuses.FirstOrDefault(
                    s => string.Equals(s.ID, value, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    return MutationResult.Rejected(
                        $"'{value}' is not a valid status. Valid statuses are: " +
                        $"{string.Join(", ", HelpDeskStatusData.Statuses.Select(s => s.ID))}.");
                }

                // Write the canonical id, not the string the model sent.
                canonicalStatus = match.ID;
            }
            else if (isDescription && string.IsNullOrWhiteSpace(value))
            {
                return MutationResult.Rejected("A ticket description must not be blank.");
            }

            if (!TryParseTicketKey(ticketId, out _))
            {
                return MutationResult.Rejected(
                    $"Ticket id '{ticketId}' is not in the expected form 'ticket:<number>'.");
            }

            return null;
        }

        MutationPreview BuildPreview(GraphDocument doc)
        {
            var node = doc.Nodes.First(n => n.Id == ticketId);
            var summary = isStatus
                ? $"Change ticket {ticketId} status from '{DataString(node, "status")}' to '{canonicalStatus}' in SQL and mirror it into the graph."
                : $"Change ticket {ticketId} description in SQL and re-truncate the node label to 60 characters.";

            return new MutationPreview(summary, PreviewFiles, Array.Empty<string>());
        }

        async Task<MutationResult?> Apply(GraphDocument doc, CancellationToken token)
        {
            TryParseTicketKey(ticketId, out var key);

            await using var db = await _contextFactory.CreateDbContextAsync(token);
            var ticket = await db.HelpDeskTickets.FirstOrDefaultAsync(t => t.Id == key, token);
            if (ticket is null)
            {
                return MutationResult.Rejected(
                    $"Ticket {key} no longer exists in the database.");
            }

            var node = doc.Nodes.First(n => n.Id == ticketId);

            if (isStatus)
            {
                ticket.TicketStatus = canonicalStatus!;
                await db.SaveChangesAsync(token);

                node.Data["status"] = canonicalStatus;
                RepointStatusEdge(doc, node, canonicalStatus!);
            }
            else
            {
                ticket.TicketDescription = value;
                await db.SaveChangesAsync(token);

                node.Label = Truncate(value, 60);
            }

            return null;
        }

        return ExecuteAsync(
            Validate, BuildPreview, Apply,
            $"UpdateTicket id={ticketId} property={propertyName.ToLowerInvariant()}",
            confirmed, ct);
    }

    // ----- Knowledge-layer content edit (graph only) -----

    public Task<MutationResult> UpdateNodeContent(
        string nodeId, string propertyName, string value,
        bool confirmed = false, CancellationToken ct = default)
    {
        var isLabel = string.Equals(propertyName, "label", StringComparison.OrdinalIgnoreCase);

        MutationResult? Validate(GraphDocument doc)
        {
            var node = doc.Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node is null)
            {
                return MutationResult.Rejected($"No node with id '{nodeId}' exists.");
            }

            if (ReadOnlyNodeTypes.Contains(node.Type))
            {
                return MutationResult.Rejected(
                    $"Nodes of type {node.Type} are read-only and cannot be edited.");
            }

            return null;
        }

        MutationPreview BuildPreview(GraphDocument doc)
        {
            var target = isLabel ? "label" : $"Data property '{propertyName}'";
            var summary = $"Set the {target} of node {nodeId} to '{value}' in the graph.";
            return new MutationPreview(summary, PreviewFiles, Array.Empty<string>());
        }

        Task<MutationResult?> Apply(GraphDocument doc, CancellationToken token)
        {
            var node = doc.Nodes.First(n => n.Id == nodeId);
            if (isLabel)
            {
                node.Label = value;
            }
            else
            {
                node.Data[propertyName] = value;
            }

            return Task.FromResult<MutationResult?>(null);
        }

        return ExecuteAsync(
            Validate, BuildPreview, Apply,
            $"UpdateNodeContent id={nodeId} property={propertyName}",
            confirmed, ct);
    }

    // ----- Knowledge-layer structure edits (not exposed to the model) -----

    public Task<MutationResult> LinkTickets(
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

            var duplicate = doc.Edges.Any(e => e.Type == "LINKED_TO"
                && ((e.From == fromTicketId && e.To == toTicketId)
                    || (e.From == toTicketId && e.To == fromTicketId)));
            if (duplicate)
            {
                return MutationResult.Rejected(
                    $"A LINKED_TO edge already connects {fromTicketId} and {toTicketId}.");
            }

            return null;
        }

        MutationPreview BuildPreview(GraphDocument doc) => new(
            $"Add a LINKED_TO edge from {fromTicketId} to {toTicketId}.",
            PreviewFiles, Array.Empty<string>());

        Task<MutationResult?> Apply(GraphDocument doc, CancellationToken token)
        {
            doc.Edges.Add(new GraphEdge
            {
                From = fromTicketId,
                To = toTicketId,
                Type = "LINKED_TO",
            });

            return Task.FromResult<MutationResult?>(null);
        }

        return ExecuteAsync(
            Validate, BuildPreview, Apply,
            $"LinkTickets from={fromTicketId} to={toTicketId}",
            confirmed, ct);
    }

    public Task<MutationResult> AddKnowledgeArticle(
        string title, string content,
        bool confirmed = false, CancellationToken ct = default)
    {
        var articleId = $"article:{Guid.NewGuid()}";

        MutationResult? Validate(GraphDocument doc)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return MutationResult.Rejected("A knowledge article requires a title.");
            }

            return null;
        }

        MutationPreview BuildPreview(GraphDocument doc) => new(
            $"Create a KnowledgeArticle node {articleId} titled '{title}'.",
            PreviewFiles, Array.Empty<string>());

        Task<MutationResult?> Apply(GraphDocument doc, CancellationToken token)
        {
            doc.Nodes.Add(new GraphNode
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

            return Task.FromResult<MutationResult?>(null);
        }

        return ExecuteAsync(
            Validate, BuildPreview, Apply,
            $"AddKnowledgeArticle id={articleId}",
            confirmed, ct);
    }

    public Task<MutationResult> ReferenceArticle(
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

            var duplicate = doc.Edges.Any(e => e.Type == "REFERENCES_ARTICLE"
                && e.From == ticketId && e.To == articleId);
            if (duplicate)
            {
                return MutationResult.Rejected(
                    $"A REFERENCES_ARTICLE edge already connects {ticketId} to {articleId}.");
            }

            return null;
        }

        MutationPreview BuildPreview(GraphDocument doc) => new(
            $"Add a REFERENCES_ARTICLE edge from {ticketId} to {articleId}.",
            PreviewFiles, Array.Empty<string>());

        Task<MutationResult?> Apply(GraphDocument doc, CancellationToken token)
        {
            doc.Edges.Add(new GraphEdge
            {
                From = ticketId,
                To = articleId,
                Type = "REFERENCES_ARTICLE",
            });

            return Task.FromResult<MutationResult?>(null);
        }

        return ExecuteAsync(
            Validate, BuildPreview, Apply,
            $"ReferenceArticle ticket={ticketId} article={articleId}",
            confirmed, ct);
    }

    public Task<MutationResult> RecordResolution(
        string ticketId, string content,
        bool confirmed = false, CancellationToken ct = default)
    {
        var resolutionId = $"resolution:{Guid.NewGuid()}";

        MutationResult? Validate(GraphDocument doc)
        {
            if (!IsNodeOfType(doc, ticketId, "Ticket"))
            {
                return MutationResult.Rejected($"'{ticketId}' is not an existing Ticket node.");
            }

            return null;
        }

        MutationPreview BuildPreview(GraphDocument doc) => new(
            $"Create a Resolution node {resolutionId} and a RESOLVED_BY edge from {ticketId}.",
            PreviewFiles, Array.Empty<string>());

        Task<MutationResult?> Apply(GraphDocument doc, CancellationToken token)
        {
            doc.Nodes.Add(new GraphNode
            {
                Id = resolutionId,
                Type = "Resolution",
                Label = Truncate(content, 60),
                Data = { ["content"] = content },
            });

            doc.Edges.Add(new GraphEdge
            {
                From = ticketId,
                To = resolutionId,
                Type = "RESOLVED_BY",
            });

            return Task.FromResult<MutationResult?>(null);
        }

        return ExecuteAsync(
            Validate, BuildPreview, Apply,
            $"RecordResolution ticket={ticketId} id={resolutionId}",
            confirmed, ct);
    }

    public Task<MutationResult> DeleteArticle(
        string articleId, bool cascade = false,
        bool confirmed = false, CancellationToken ct = default)
    {
        MutationResult? Validate(GraphDocument doc)
        {
            if (!IsNodeOfType(doc, articleId, "KnowledgeArticle"))
            {
                return MutationResult.Rejected($"'{articleId}' is not an existing KnowledgeArticle node.");
            }

            var referenceCount = doc.Edges.Count(
                e => e.Type == "REFERENCES_ARTICLE" && e.To == articleId);
            if (referenceCount > 0 && !cascade)
            {
                return MutationResult.Rejected(
                    $"{articleId} still has {referenceCount} REFERENCES_ARTICLE edge(s) " +
                    "pointing to it. Pass cascade to delete it and those edges.");
            }

            return null;
        }

        MutationPreview BuildPreview(GraphDocument doc)
        {
            var referenceCount = doc.Edges.Count(
                e => e.Type == "REFERENCES_ARTICLE" && e.To == articleId);

            var warnings = referenceCount > 0
                ? new[] { $"This will also remove {referenceCount} REFERENCES_ARTICLE edge(s)." }
                : Array.Empty<string>();

            return new MutationPreview(
                $"Delete KnowledgeArticle node {articleId} and its incoming edges.",
                PreviewFiles, warnings);
        }

        Task<MutationResult?> Apply(GraphDocument doc, CancellationToken token)
        {
            doc.Edges.RemoveAll(e => e.To == articleId || e.From == articleId);
            doc.Nodes.RemoveAll(n => n.Id == articleId);

            return Task.FromResult<MutationResult?>(null);
        }

        return ExecuteAsync(
            Validate, BuildPreview, Apply,
            $"DeleteArticle id={articleId} cascade={cascade}",
            confirmed, ct);
    }

    // ----- Shared two-phase execution -----

    private async Task<MutationResult> ExecuteAsync(
        Func<GraphDocument, MutationResult?> validate,
        Func<GraphDocument, MutationPreview> buildPreview,
        Func<GraphDocument, CancellationToken, Task<MutationResult?>> apply,
        string auditDetail,
        bool confirmed,
        CancellationToken ct)
    {
        // Preview phase: read and validate without taking the lock.
        var document = await LoadDocumentAsync(ct);
        var rejection = validate(document);
        if (rejection is not null)
        {
            return rejection;
        }

        var preview = buildPreview(document);
        if (!confirmed)
        {
            return MutationResult.PreviewOnly(preview);
        }

        // Apply phase: re-load and re-validate under the process-wide lock, so
        // an approval can only ever apply to a state that still holds.
        await WriteGate.WaitAsync(ct);
        try
        {
            document = await LoadDocumentAsync(ct);
            rejection = validate(document);
            if (rejection is not null)
            {
                return rejection;
            }

            var applyRejection = await apply(document, ct);
            if (applyRejection is not null)
            {
                return applyRejection;
            }

            await CommitAsync(document, auditDetail, ct);
            return MutationResult.Applied(preview);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    private Task<GraphDocument> LoadDocumentAsync(CancellationToken ct)
    {
        var path = GraphFile.ResolvePath(_options.OutputPath, _environment.ContentRootPath);
        return GraphFile.LoadAsync(path, ct);
    }

    private async Task CommitAsync(
        GraphDocument document, string auditDetail, CancellationToken ct)
    {
        var directory = GraphFile.ResolveDirectory(
            _options.OutputPath, _environment.ContentRootPath);
        var graphPath = Path.Combine(directory, GraphFile.GraphFileName);
        var mutationUtc = DateTime.UtcNow;

        await GraphFile.SaveAtomicAsync(document, graphPath, ct);
        await GraphFile.WriteManifestAsync(document, directory, mutationUtc, ct);
        await _store.ReloadAsync(ct);

        var line = string.Concat(
            mutationUtc.ToString("O", CultureInfo.InvariantCulture),
            "\t",
            auditDetail,
            Environment.NewLine);
        var auditPath = Path.Combine(directory, AuditFileName);
        await File.AppendAllTextAsync(auditPath, line, ct);
    }

    // ----- Helpers -----

    private static void RepointStatusEdge(
        GraphDocument document, GraphNode ticket, string status)
    {
        var statusId = $"status:{status.ToLowerInvariant()}";

        // Create the Status node when no ticket currently holds that status.
        if (!document.Nodes.Any(n => n.Id == statusId))
        {
            document.Nodes.Add(new GraphNode
            {
                Id = statusId,
                Type = "Status",
                Label = status,
                Data = { ["status"] = status },
            });
        }

        document.Edges.RemoveAll(e => e.Type == "HAS_STATUS" && e.From == ticket.Id);
        document.Edges.Add(new GraphEdge
        {
            From = ticket.Id,
            To = statusId,
            Type = "HAS_STATUS",
        });
    }

    private static bool IsNodeOfType(GraphDocument document, string id, string type) =>
        document.Nodes.Any(n => n.Id == id && n.Type == type);

    private static bool TryParseTicketKey(string ticketId, out int key)
    {
        key = 0;
        const string prefix = "ticket:";
        if (!ticketId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(
            ticketId.AsSpan(prefix.Length), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out key);
    }

    private static string DataString(GraphNode node, string key) =>
        node.Data.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        return text.Length <= max ? text : text[..max];
    }
}
