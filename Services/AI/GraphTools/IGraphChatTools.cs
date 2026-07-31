using System.ComponentModel;

namespace SyncfusionHelpDesk.Services.AI.GraphTools;

/// <summary>
/// Read-only tool contract over the knowledge graph. Every method is a
/// deterministic <c>GraphStore</c> traversal. The <see cref="DescriptionAttribute"/>
/// text lives here, on the interface, so the schema the model reads and the
/// contract the code compiles against cannot drift apart.
/// </summary>
public interface IGraphChatTools
{
    [Description("Finds Requester nodes whose email address contains the given text (case-insensitive). Use this first to resolve a person before looking up their tickets.")]
    IList<RequesterSummary> FindRequesterByEmail(
        [Description("Text to match anywhere in the requester email address.")] string query,
        [Description("Maximum number of requesters to return.")] int max = 20);

    [Description("Counts how many tickets a requester has opened, identified by their exact email address.")]
    int CountTicketsForRequester(
        [Description("The exact requester email address.")] string email);

    [Description("Lists the tickets opened by a requester, identified by their exact email address, optionally filtered to a single status.")]
    IList<TicketSummary> ListTicketsForRequester(
        [Description("The exact requester email address.")] string email,
        [Description("Optional status filter such as Open, Closed, New, Pending, or Urgent. Leave null for all statuses.")] string? status = null,
        [Description("Maximum number of tickets to return.")] int max = 20);

    [Description("Lists every ticket that currently has the given status, such as Open, Closed, New, Pending, or Urgent. This is the only correct way to answer questions about tickets with a particular status.")]
    IList<TicketSummary> ListTicketsByStatus(
        [Description("The status to match, such as Open, Closed, New, Pending, or Urgent.")] string status,
        [Description("Maximum number of tickets to return.")] int max = 20);

    [Description("Lists every status that exists in the graph together with how many tickets have that status. Use this to confirm a status exists before saying no tickets have it.")]
    IList<StatusSummary> ListStatuses();

    [Description("Lists the detail comments attached to a ticket, identified by its node id such as 'ticket:42'.")]
    IList<CommentSummary> ListDetailsForTicket(
        [Description("The ticket node id, for example 'ticket:42'.")] string ticketId,
        [Description("Maximum number of detail comments to return.")] int max = 20);

    [Description("Searches nodes by matching the text against a node id, its label, or any of its Data values (case-insensitive). Optionally restricts the search to a single node type. Prefer the purpose-built tools over this generic search.")]
    IList<NodeSummary> SearchNodes(
        [Description("Text to match against node id, label, or Data values.")] string query,
        [Description("Optional node type filter such as Ticket, TicketDetail, Requester, Status, or Day. Leave null to search all types.")] string? type = null,
        [Description("Maximum number of nodes to return.")] int max = 20);

    [Description("Returns the full detail of a single node, including all of its Data values, identified by its node id such as 'ticket:42'.")]
    NodeDetail? GetNode(
        [Description("The node id, for example 'ticket:42' or 'status:open'.")] string id);

    [Description("Returns the neighbouring nodes of a node, identified by its node id, optionally filtered to a single edge type.")]
    IList<Neighbor> GetNeighbors(
        [Description("The node id, for example 'ticket:42'.")] string id,
        [Description("Optional edge type filter such as REQUESTED_BY, HAS_DETAIL, HAS_STATUS, or OCCURRED_ON. Leave null for all edge types.")] string? edgeType = null,
        [Description("Maximum number of neighbours to return.")] int max = 20);

    [Description("Returns overall graph statistics: total node and edge counts, plus counts broken down by node type and edge type.")]
    GraphStats Stats();
}
