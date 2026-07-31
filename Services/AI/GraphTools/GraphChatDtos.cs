namespace SyncfusionHelpDesk.Services.AI.GraphTools;

/// <summary>Lightweight node projection returned by search tools.</summary>
public sealed record NodeSummary(string Id, string Type, string Label);

/// <summary>Full node projection including its data properties.</summary>
public sealed record NodeDetail(
    string Id,
    string Type,
    string Label,
    IReadOnlyDictionary<string, string?> Data);

/// <summary>A node reached from another node across a single edge.</summary>
public sealed record Neighbor(
    string EdgeType,
    string Direction,
    string NodeId,
    string NodeType,
    string NodeLabel);

/// <summary>A Ticket node projected for chat answers.</summary>
public sealed record TicketSummary(
    string Id,
    string Label,
    string? Status,
    string? TicketDate,
    string? RequesterEmail);

/// <summary>A TicketDetail (comment) node projected for chat answers.</summary>
public sealed record CommentSummary(string Id, string? Snippet, string? Date);

/// <summary>A Requester node projected for chat answers.</summary>
public sealed record RequesterSummary(string Id, string Email, string Label);

/// <summary>Node and edge counts for the whole graph.</summary>
public sealed record GraphStats(
    int TotalNodes,
    int TotalEdges,
    IReadOnlyDictionary<string, int> NodesByType,
    IReadOnlyDictionary<string, int> EdgesByType);
