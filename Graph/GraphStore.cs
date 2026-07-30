using Microsoft.Extensions.Options;

namespace SyncfusionHelpDesk.Graph;

public sealed class GraphStore
{
    private readonly GraphOptions _options;
    private readonly IWebHostEnvironment _environment;

    private GraphDocument _document = new();
    private Dictionary<string, GraphNode> _byId = new(StringComparer.Ordinal);
    private ILookup<string, GraphNode> _byType = Enumerable.Empty<GraphNode>().ToLookup(n => n.Type);
    private ILookup<string, GraphEdge> _byFrom = Enumerable.Empty<GraphEdge>().ToLookup(e => e.From);
    private ILookup<string, GraphEdge> _byTo = Enumerable.Empty<GraphEdge>().ToLookup(e => e.To);

    public GraphStore(IOptions<GraphOptions> options, IWebHostEnvironment environment)
    {
        _options = options.Value;
        _environment = environment;
    }

    public IReadOnlyList<GraphNode> Nodes => _document.Nodes;

    public IReadOnlyList<GraphEdge> Edges => _document.Edges;

    public DateTime GeneratedUtc => _document.GeneratedUtc;

    public GraphNode? GetNode(string id) =>
        _byId.TryGetValue(id, out var node) ? node : null;

    public IEnumerable<GraphNode> NodesOfType(string type) => _byType[type];

    public IEnumerable<GraphEdge> OutEdges(string id) => _byFrom[id];

    public IEnumerable<GraphEdge> InEdges(string id) => _byTo[id];

    public IEnumerable<string> NodeTypes => _byType.Select(g => g.Key);

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var path = GraphFile.ResolvePath(_options.OutputPath, _environment.ContentRootPath);
        var document = await GraphFile.LoadAsync(path, cancellationToken);
        SetDocument(document);
    }

    public void SetDocument(GraphDocument document)
    {
        _document = document;
        _byId = document.Nodes
            .GroupBy(n => n.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _byType = document.Nodes.ToLookup(n => n.Type);
        _byFrom = document.Edges.ToLookup(e => e.From);
        _byTo = document.Edges.ToLookup(e => e.To);
    }
}
