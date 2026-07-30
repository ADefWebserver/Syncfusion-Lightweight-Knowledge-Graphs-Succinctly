namespace SyncfusionHelpDesk.Graph;

public sealed class GraphDocument
{
    public int Version { get; set; } = 1;
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    public List<GraphNode> Nodes { get; set; } = new();
    public List<GraphEdge> Edges { get; set; } = new();
}

public sealed class GraphNode
{
    public string Id { get; set; } = "";        // e.g. "ticket:42"
    public string Type { get; set; } = "";       // e.g. "Ticket"
    public string Label { get; set; } = "";      // display text
    public Dictionary<string, object?> Data { get; set; } = new();
}

public sealed class GraphEdge
{
    public string From { get; set; } = "";       // node id
    public string To { get; set; } = "";         // node id
    public string Type { get; set; } = "";        // e.g. "REQUESTED_BY"
    public Dictionary<string, object?> Data { get; set; } = new();
}
