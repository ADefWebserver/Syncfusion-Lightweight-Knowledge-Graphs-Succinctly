using System.ComponentModel;

namespace SyncfusionHelpDesk.Services.AI.GraphTools;

/// <summary>
/// Read-only traversal contract over the help-desk knowledge graph. The
/// <see cref="DescriptionAttribute"/> text lives here, on the interface, so the
/// tool schema the model reads and the code the implementation compiles against
/// stay in sync.
/// </summary>
public interface IGraphChatTools
{
    [Description("Find requester nodes whose email address contains the query text (case-insensitive). Use this to resolve a person before looking up their tickets.")]
    IList<RequesterSummary> FindRequesterByEmail(
        [Description("Full or partial email address to search for.")] string query,
        [Description("Maximum number of requesters to return.")] int max = 10);

    [Description("Count how many tickets a requester has opened, identified by their exact email address.")]
    int CountTicketsForRequester(
        [Description("The exact email address of the requester.")] string email);

    [Description("List the tickets opened by a requester, identified by their exact email address, optionally filtered by status.")]
    IList<TicketSummary> ListTicketsForRequester(
        [Description("The exact email address of the requester.")] string email,
        [Description("Optional ticket status to filter by (for example 'New' or 'Closed'). Leave empty for all statuses.")] string? status = null,
        [Description("Maximum number of tickets to return.")] int max = 20);

    [Description("List the detail comments attached to a ticket, identified by its ticket id (for example 'ticket:42').")]
    IList<CommentSummary> ListDetailsForTicket(
        [Description("The ticket id, in the form 'ticket:<number>'.")] string ticketId,
        [Description("Maximum number of detail comments to return.")] int max = 20);

    [Description("Search graph nodes whose id or label contains the query text (case-insensitive), optionally restricted to a node type.")]
    IList<NodeSummary> SearchNodes(
        [Description("Text to search for within node ids and labels.")] string query,
        [Description("Optional node type to restrict the search to (Ticket, TicketDetail, Requester, Status, or Day).")] string? type = null,
        [Description("Maximum number of nodes to return.")] int max = 20);

    [Description("Get a single node and its data properties by its exact id (for example 'ticket:42' or 'requester:user@example.com').")]
    NodeDetail? GetNode(
        [Description("The exact node id, in the form '<type>:<key>'.")] string id);

    [Description("Get the neighbors of a node by its exact id, optionally filtered by edge type. Returns both incoming and outgoing edges.")]
    IList<Neighbor> GetNeighbors(
        [Description("The exact node id, in the form '<type>:<key>'.")] string id,
        [Description("Optional edge type to filter by (REQUESTED_BY, HAS_DETAIL, HAS_STATUS, or OCCURRED_ON).")] string? edgeType = null,
        [Description("Maximum number of neighbors to return.")] int max = 20);

    [Description("Get overall graph statistics: total node and edge counts, plus counts broken down by node type and edge type.")]
    GraphStats Stats();
}
