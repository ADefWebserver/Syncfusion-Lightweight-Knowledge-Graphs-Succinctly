using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyncfusionHelpDesk.Models;

namespace SyncfusionHelpDesk.Graph;

public sealed class HelpDeskGraphBuilder
{
    private readonly IDbContextFactory<SyncfusionHelpDeskContext> _contextFactory;
    private readonly GraphOptions _options;
    private readonly IWebHostEnvironment _environment;

    public HelpDeskGraphBuilder(
        IDbContextFactory<SyncfusionHelpDeskContext> contextFactory,
        IOptions<GraphOptions> options,
        IWebHostEnvironment environment)
    {
        _contextFactory = contextFactory;
        _options = options.Value;
        _environment = environment;
    }

    /// <summary>Number of candidate edges skipped in the last build because an endpoint was missing.</summary>
    public int LastSkippedEdges { get; private set; }

    public async Task<GraphDocument> BuildAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var tickets = await context.HelpDeskTickets
            .AsNoTracking()
            .Include(t => t.HelpDeskTicketDetails)
            .ToListAsync(cancellationToken);

        var document = new GraphDocument();
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);

        // --- Emit all nodes first (steps 3-5), de-duplicated through the HashSet. ---

        // Step 3: Requester nodes.
        foreach (var ticket in tickets)
        {
            AddRequesterNode(document, nodeIds, ticket.TicketRequesterEmail);
        }

        // Step 4: Ticket and TicketDetail nodes.
        foreach (var ticket in tickets)
        {
            AddTicketNode(document, nodeIds, ticket);

            foreach (var detail in ticket.HelpDeskTicketDetails)
            {
                AddDetailNode(document, nodeIds, detail);
            }
        }

        // Step 5: Day and Status nodes.
        foreach (var ticket in tickets)
        {
            AddDayNode(document, nodeIds, ticket.TicketDate);

            foreach (var detail in ticket.HelpDeskTicketDetails)
            {
                AddDayNode(document, nodeIds, detail.TicketDetailDate);
            }

            AddStatusNode(document, nodeIds, ticket.TicketStatus);
        }

        // --- Node set complete. Build and validate candidate edges (steps 6-7). ---
        var skipped = 0;

        foreach (var ticket in tickets)
        {
            var ticketId = TicketId(ticket.Id);

            AddEdgeIfValid(document, nodeIds, ref skipped, new GraphEdge
            {
                From = ticketId,
                To = RequesterId(ticket.TicketRequesterEmail),
                Type = "REQUESTED_BY",
            });

            AddEdgeIfValid(document, nodeIds, ref skipped, new GraphEdge
            {
                From = ticketId,
                To = StatusId(ticket.TicketStatus),
                Type = "HAS_STATUS",
            });

            AddEdgeIfValid(document, nodeIds, ref skipped, new GraphEdge
            {
                From = ticketId,
                To = DayId(ticket.TicketDate),
                Type = "OCCURRED_ON",
                Data = { ["date"] = ticket.TicketDate },
            });

            foreach (var detail in ticket.HelpDeskTicketDetails)
            {
                var detailId = DetailId(detail.Id);

                AddEdgeIfValid(document, nodeIds, ref skipped, new GraphEdge
                {
                    From = ticketId,
                    To = detailId,
                    Type = "HAS_DETAIL",
                    Data = { ["detailDate"] = detail.TicketDetailDate },
                });

                AddEdgeIfValid(document, nodeIds, ref skipped, new GraphEdge
                {
                    From = detailId,
                    To = DayId(detail.TicketDetailDate),
                    Type = "OCCURRED_ON",
                    Data = { ["date"] = detail.TicketDetailDate },
                });
            }
        }

        LastSkippedEdges = skipped;
        document.GeneratedUtc = DateTime.UtcNow;

        // Persist the three sibling files.
        var directory = GraphFile.ResolveDirectory(
            _options.OutputPath, _environment.ContentRootPath);
        var graphPath = Path.Combine(directory, GraphFile.GraphFileName);

        await GraphFile.SaveAtomicAsync(document, graphPath, cancellationToken);
        await GraphFile.WriteManifestAsync(document, directory, cancellationToken: cancellationToken);
        await GraphFile.WriteMetadataAsync(directory, cancellationToken);

        return document;
    }

    // ----- Node helpers -----

    private static void AddRequesterNode(
        GraphDocument document, HashSet<string> nodeIds, string? email)
    {
        var id = RequesterId(email);
        if (!nodeIds.Add(id))
        {
            return;
        }

        document.Nodes.Add(new GraphNode
        {
            Id = id,
            Type = "Requester",
            Label = email ?? "",
            Data = { ["email"] = email },
        });
    }

    private static void AddTicketNode(
        GraphDocument document, HashSet<string> nodeIds, HelpDeskTicket ticket)
    {
        var id = TicketId(ticket.Id);
        if (!nodeIds.Add(id))
        {
            return;
        }

        document.Nodes.Add(new GraphNode
        {
            Id = id,
            Type = "Ticket",
            Label = Truncate(ticket.TicketDescription, 60),
            Data =
            {
                ["status"] = ticket.TicketStatus,
                ["ticketDate"] = ticket.TicketDate,
                ["requesterEmail"] = ticket.TicketRequesterEmail,
                ["ticketGuid"] = ticket.TicketGuid,
            },
        });
    }

    private static void AddDetailNode(
        GraphDocument document, HashSet<string> nodeIds, HelpDeskTicketDetail detail)
    {
        var id = DetailId(detail.Id);
        if (!nodeIds.Add(id))
        {
            return;
        }

        document.Nodes.Add(new GraphNode
        {
            Id = id,
            Type = "TicketDetail",
            Label = Truncate(detail.TicketDescription, 60),
            Data =
            {
                ["ticketDetailDate"] = detail.TicketDetailDate,
                ["snippet"] = Truncate(detail.TicketDescription, 200),
            },
        });
    }

    private static void AddStatusNode(
        GraphDocument document, HashSet<string> nodeIds, string? status)
    {
        var id = StatusId(status);
        if (!nodeIds.Add(id))
        {
            return;
        }

        document.Nodes.Add(new GraphNode
        {
            Id = id,
            Type = "Status",
            Label = status ?? "",
            Data = { ["status"] = status },
        });
    }

    private static void AddDayNode(
        GraphDocument document, HashSet<string> nodeIds, DateTime date)
    {
        var key = DayKey(date);
        var id = DayId(date);
        if (!nodeIds.Add(id))
        {
            return;
        }

        document.Nodes.Add(new GraphNode
        {
            Id = id,
            Type = "Day",
            Label = key,
            Data = { ["date"] = key },
        });
    }

    // ----- Edge helper -----

    private static void AddEdgeIfValid(
        GraphDocument document, HashSet<string> nodeIds, ref int skipped, GraphEdge edge)
    {
        if (nodeIds.Contains(edge.From) && nodeIds.Contains(edge.To))
        {
            document.Edges.Add(edge);
        }
        else
        {
            skipped++;
        }
    }

    // ----- ID helpers (all lowercase, pattern <type>:<key>) -----

    private static string TicketId(int id) => $"ticket:{id}";

    private static string DetailId(int id) => $"detail:{id}";

    private static string RequesterId(string? email) =>
        $"requester:{(email ?? "").ToLowerInvariant()}";

    private static string StatusId(string? status) =>
        $"status:{(status ?? "").ToLowerInvariant()}";

    private static string DayId(DateTime date) => $"day:{DayKey(date)}";

    private static string DayKey(DateTime date) =>
        date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        return text.Length <= max ? text : text[..max];
    }
}
