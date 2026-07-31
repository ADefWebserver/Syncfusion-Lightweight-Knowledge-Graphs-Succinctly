using System.Text.Json;
using SyncfusionHelpDesk.Graph;

namespace SyncfusionHelpDesk.Services.AI.GraphTools;

/// <summary>
/// Deterministic, read-only implementation of <see cref="IGraphChatTools"/>.
/// Every method is a pure traversal over the singleton <see cref="GraphStore"/>;
/// nothing here mutates the graph.
/// </summary>
public sealed class GraphChatTools : IGraphChatTools
{
    private readonly GraphStore _store;

    public GraphChatTools(GraphStore store) => _store = store;

    public IList<RequesterSummary> FindRequesterByEmail(string query, int max = 10)
    {
        query ??= "";

        return _store.NodesOfType("Requester")
            .Where(n => DataString(n, "email")
                .Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(Clamp(max))
            .Select(n => new RequesterSummary(n.Id, DataString(n, "email"), n.Label))
            .ToList();
    }

    public int CountTicketsForRequester(string email)
    {
        var id = RequesterId(email);

        return _store.InEdges(id)
            .Count(e => e.Type == "REQUESTED_BY");
    }

    public IList<TicketSummary> ListTicketsForRequester(
        string email, string? status = null, int max = 20)
    {
        var id = RequesterId(email);

        var tickets = _store.InEdges(id)
            .Where(e => e.Type == "REQUESTED_BY")
            .Select(e => _store.GetNode(e.From))
            .Where(n => n is not null)
            .Select(n => n!);

        if (!string.IsNullOrWhiteSpace(status))
        {
            tickets = tickets.Where(n =>
                DataString(n, "status").Equals(status, StringComparison.OrdinalIgnoreCase));
        }

        return tickets
            .Take(Clamp(max))
            .Select(ToTicketSummary)
            .ToList();
    }

    public IList<CommentSummary> ListDetailsForTicket(string ticketId, int max = 20)
    {
        return _store.OutEdges(ticketId)
            .Where(e => e.Type == "HAS_DETAIL")
            .Select(e => _store.GetNode(e.To))
            .Where(n => n is not null && n.Type == "TicketDetail")
            .Select(n => n!)
            .Take(Clamp(max))
            .Select(n => new CommentSummary(
                n.Id,
                DataStringOrNull(n, "snippet") ?? n.Label,
                DataStringOrNull(n, "ticketDetailDate")))
            .ToList();
    }

    public IList<NodeSummary> SearchNodes(string query, string? type = null, int max = 20)
    {
        query ??= "";

        var nodes = string.IsNullOrWhiteSpace(type)
            ? (IEnumerable<GraphNode>)_store.Nodes
            : _store.NodesOfType(type);

        return nodes
            .Where(n =>
                n.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                n.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(Clamp(max))
            .Select(n => new NodeSummary(n.Id, n.Type, n.Label))
            .ToList();
    }

    public NodeDetail? GetNode(string id)
    {
        var node = _store.GetNode(id);
        if (node is null)
        {
            return null;
        }

        var data = node.Data.ToDictionary(
            kvp => kvp.Key,
            kvp => ValueToString(kvp.Value),
            StringComparer.Ordinal);

        return new NodeDetail(node.Id, node.Type, node.Label, data);
    }

    public IList<Neighbor> GetNeighbors(string id, string? edgeType = null, int max = 20)
    {
        var neighbors = new List<Neighbor>();

        foreach (var edge in _store.OutEdges(id))
        {
            if (!MatchesEdgeType(edge.Type, edgeType))
            {
                continue;
            }

            var target = _store.GetNode(edge.To);
            neighbors.Add(new Neighbor(
                edge.Type, "outgoing", edge.To,
                target?.Type ?? "", target?.Label ?? ""));
        }

        foreach (var edge in _store.InEdges(id))
        {
            if (!MatchesEdgeType(edge.Type, edgeType))
            {
                continue;
            }

            var source = _store.GetNode(edge.From);
            neighbors.Add(new Neighbor(
                edge.Type, "incoming", edge.From,
                source?.Type ?? "", source?.Label ?? ""));
        }

        return neighbors.Take(Clamp(max)).ToList();
    }

    public GraphStats Stats()
    {
        var nodesByType = _store.Nodes
            .GroupBy(n => n.Type)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var edgesByType = _store.Edges
            .GroupBy(e => e.Type)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return new GraphStats(
            _store.Nodes.Count,
            _store.Edges.Count,
            nodesByType,
            edgesByType);
    }

    // ----- Helpers -----

    private TicketSummary ToTicketSummary(GraphNode node) => new(
        node.Id,
        node.Label,
        DataStringOrNull(node, "status"),
        DataStringOrNull(node, "ticketDate"),
        DataStringOrNull(node, "requesterEmail"));

    private static bool MatchesEdgeType(string edgeType, string? filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        edgeType.Equals(filter, StringComparison.OrdinalIgnoreCase);

    private static string RequesterId(string? email) =>
        $"requester:{(email ?? "").ToLowerInvariant()}";

    private static int Clamp(int max) => max <= 0 ? 1 : max;

    private static string DataString(GraphNode node, string key) =>
        DataStringOrNull(node, key) ?? "";

    private static string? DataStringOrNull(GraphNode node, string key) =>
        node.Data.TryGetValue(key, out var value) ? ValueToString(value) : null;

    private static string? ValueToString(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => element.GetString(),
                _ => element.ToString(),
            };
        }

        return value.ToString();
    }
}
