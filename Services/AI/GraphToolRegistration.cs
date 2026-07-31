using Microsoft.Extensions.AI;
using SyncfusionHelpDesk.Services.AI.GraphTools;

namespace SyncfusionHelpDesk.Services.AI;

/// <summary>
/// Turns the <see cref="IGraphChatTools"/> contract into the list of
/// <see cref="AITool"/> instances the chat client invokes automatically. The
/// method groups are bound through the interface, so the schema comes from the
/// <c>[Description]</c> attributes declared on the interface.
/// </summary>
public static class GraphToolRegistration
{
    public static IList<AITool> CreateTools(IGraphChatTools tools) => new List<AITool>
    {
        AIFunctionFactory.Create(tools.FindRequesterByEmail),
        AIFunctionFactory.Create(tools.CountTicketsForRequester),
        AIFunctionFactory.Create(tools.ListTicketsForRequester),
        AIFunctionFactory.Create(tools.ListTicketsByStatus),
        AIFunctionFactory.Create(tools.ListStatuses),
        AIFunctionFactory.Create(tools.ListDetailsForTicket),
        AIFunctionFactory.Create(tools.SearchNodes),
        AIFunctionFactory.Create(tools.GetNode),
        AIFunctionFactory.Create(tools.GetNeighbors),
        AIFunctionFactory.Create(tools.Stats),
    };

    /// <summary>
    /// Appends the two write tools to the read-only traversal tools. The
    /// <c>/graphchat</c> page must call this overload; using the single-argument
    /// read-only overload is a silent failure that leaves the model unable to
    /// propose changes.
    /// </summary>
    public static IList<AITool> CreateTools(
        IGraphChatTools read, IGraphWriteTools write)
    {
        var tools = CreateTools(read);
        tools.Add(AIFunctionFactory.Create(write.UpdateNodeContent));
        tools.Add(AIFunctionFactory.Create(write.UpdateTicket));
        return tools;
    }
}
