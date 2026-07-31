using Microsoft.Extensions.AI;

namespace SyncfusionHelpDesk.Services.AI.GraphTools;

/// <summary>
/// Wraps each <see cref="IGraphChatTools"/> method as an <see cref="AITool"/>
/// so the chat client can invoke them automatically during a request.
/// </summary>
public static class GraphToolRegistration
{
    public static IList<AITool> CreateTools(IGraphChatTools tools) => new List<AITool>
    {
        AIFunctionFactory.Create(tools.FindRequesterByEmail),
        AIFunctionFactory.Create(tools.CountTicketsForRequester),
        AIFunctionFactory.Create(tools.ListTicketsForRequester),
        AIFunctionFactory.Create(tools.ListDetailsForTicket),
        AIFunctionFactory.Create(tools.SearchNodes),
        AIFunctionFactory.Create(tools.GetNode),
        AIFunctionFactory.Create(tools.GetNeighbors),
        AIFunctionFactory.Create(tools.Stats),
    };
}
