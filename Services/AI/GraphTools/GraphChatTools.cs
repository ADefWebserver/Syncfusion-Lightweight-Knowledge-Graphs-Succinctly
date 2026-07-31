using System.Text.Json;
using SyncfusionHelpDesk.Graph;

namespace SyncfusionHelpDesk.Services.AI.GraphTools;

/// <summary>
/// Read-only implementation of <see cref="IGraphChatTools"/>. Every method is a
/// deterministic traversal of the singleton <see cref="GraphStore"/>; no method
/// mutates the graph.
/// </summary>
public sealed class GraphChatTools : IGraphChatTools
{
    private readonly GraphStore _store;

    public GraphChatTools(GraphStore store) => _store = store;

    public IList<RequesterSummary> FindRequesterByEmail(string query, int max = 20)
    {
        query ??= "";

        return _store.NodesOfType("Requester")
            .Where(n => Value(n.Data, "email").Contains(query, StringComparison.OrdinalIgnoreCase)
                        || n.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(Clamp(max))
            .Select(n => new RequesterSummary(n.Id, Value(n.Data, "email"), n.Label))
            .ToList();
    }

    public int CountTicketsForRequester(string email)
    {
        var id = RequesterId(email);

        return _store.InEdges(id).Count(e => e.Type == "REQUESTED_BY");
    }

    public IList<TicketSummary> ListTicketsForRequester(
        string email, string? status = null, int max = 20)
    {
        var id = RequesterId(email);

        return _store.InEdges(id)
            .Where(e => e.Type == "REQUESTED_BY")
            .Select(e => _store.GetNode(e.From))
            .Where(n => n is not null)
            .Select(n => n!)
            .Where(n => status is null
                        || string.Equals(Value(n.Data, "status"), status, StringComparison.OrdinalIgnoreCase))
            .Take(Clamp(max))
            .Select(ToTicketSummary)
            .ToList();
    }

    public IList<TicketSummary> ListTicketsByStatus(string status, int max = 20)
    {
        var id = StatusId(status);

        // O(1) lookup then a short walk along incoming HAS_STATUS edges back to
        // the Ticket nodes; an absent status simply yields an empty list.
        if (_store.GetNode(id) is null)
        {
            return new List<TicketSummary>();
        }

        return _store.InEdges(id)
            .Where(e => e.Type == "HAS_STATUS")
            .Select(e => _store.GetNode(e.From))
            .Where(n => n is not null)
            .Select(n => n!)
            .Take(Clamp(max))
            .Select(ToTicketSummary)
            .ToList();
    }

    public IList<StatusSummary> ListStatuses()
    {
        return _store.NodesOfType("Status")
            .Select(n => new StatusSummary(
                n.Id,
                Value(n.Data, "status") is { Length: > 0 } s ? s : n.Label,
                _store.InEdges(n.Id).Count(e => e.Type == "HAS_STATUS")))
            .ToList();
    }

    public IList<CommentSummary> ListDetailsForTicket(string ticketId, int max = 20)
    {
        return _store.OutEdges(ticketId)
            .Where(e => e.Type == "HAS_DETAIL")
            .Select(e => _store.GetNode(e.To))
            .Where(n => n is not null)
            .Select(n => n!)
            .Take(Clamp(max))
            .Select(n => new CommentSummary(
                n.Id,
                Value(n.Data, "snippet") is { Length: > 0 } snippet ? snippet : n.Label,
                NullableValue(n.Data, "ticketDetailDate")))
            .ToList();
    }

    public IList<NodeSummary> SearchNodes(string query, string? type = null, int max = 20)
    {
        query ??= "";

        var candidates = string.IsNullOrWhiteSpace(type)
            ? _store.Nodes.AsEnumerable()
            : _store.NodesOfType(type);

        return candidates
            .Where(n => n.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || n.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || n.Data.Values.Any(v =>
                            ToStringValue(v).Contains(query, StringComparison.OrdinalIgnoreCase)))
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
            kvp => (string?)NullableToStringValue(kvp.Value));

        return new NodeDetail(node.Id, node.Type, node.Label, data);
    }

    public IList<Neighbor> GetNeighbors(string id, string? edgeType = null, int max = 20)
    {
        var outgoing = _store.OutEdges(id)
            .Where(e => edgeType is null || e.Type == edgeType)
            .Select(e => (Edge: e, Direction: "out", NodeId: e.To));

        var incoming = _store.InEdges(id)
            .Where(e => edgeType is null || e.Type == edgeType)
            .Select(e => (Edge: e, Direction: "in", NodeId: e.From));

        return outgoing.Concat(incoming)
            .Take(Clamp(max))
            .Select(x =>
            {
                var node = _store.GetNode(x.NodeId);
                return new Neighbor(
                    x.Edge.Type,
                    x.Direction,
                    x.NodeId,
                    node?.Type ?? "",
                    node?.Label ?? "");
            })
            .ToList();
    }

    public GraphStats Stats()
    {
        var nodesByType = _store.Nodes
            .GroupBy(n => n.Type)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

        var edgesByType = _store.Edges
            .GroupBy(e => e.Type)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

        return new GraphStats(
            _store.Nodes.Count,
            _store.Edges.Count,
            nodesByType,
            edgesByType);
    }

    // ----- Helpers -----

    private static TicketSummary ToTicketSummary(GraphNode n) => new(
        n.Id,
        n.Label,
        NullableValue(n.Data, "status"),
        NullableValue(n.Data, "ticketDate"),
        NullableValue(n.Data, "requesterEmail"));

    private static string RequesterId(string? email) =>
        $"requester:{(email ?? "").ToLowerInvariant()}";

    private static string StatusId(string? status) =>
        $"status:{(status ?? "").ToLowerInvariant()}";

    private static int Clamp(int max) => max <= 0 ? 20 : max;

    private static string Value(IReadOnlyDictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var v) ? ToStringValue(v) : "";

    private static string? NullableValue(IReadOnlyDictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var v) ? NullableToStringValue(v) : null;

    private static string ToStringValue(object? value) =>
        NullableToStringValue(value) ?? "";

    private static string? NullableToStringValue(object? value)
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
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => element.GetRawText(),
            };
        }

        return value.ToString();
    }
}
