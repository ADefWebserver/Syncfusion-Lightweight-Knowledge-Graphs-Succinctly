namespace SyncfusionHelpDesk.Services.AI.GraphTools;

public sealed record NodeSummary(string Id, string Type, string Label);

public sealed record NodeDetail(string Id, string Type, string Label,
    IReadOnlyDictionary<string, string?> Data);

public sealed record Neighbor(string EdgeType, string Direction,
    string NodeId, string NodeType, string NodeLabel);

public sealed record TicketSummary(string Id, string Label, string? Status,
    string? TicketDate, string? RequesterEmail);

public sealed record CommentSummary(string Id, string? Snippet, string? Date);

public sealed record RequesterSummary(string Id, string Email, string Label);

public sealed record StatusSummary(string Id, string Status, int TicketCount);

public sealed record GraphStats(int TotalNodes, int TotalEdges,
    IReadOnlyDictionary<string, int> NodesByType,
    IReadOnlyDictionary<string, int> EdgesByType);
