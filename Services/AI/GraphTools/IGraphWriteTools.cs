using System.ComponentModel;
using SyncfusionHelpDesk.Services.Graph;

namespace SyncfusionHelpDesk.Services.AI.GraphTools;

public enum MutationKind { NodeContent, Ticket }

public sealed record PendingMutation(
    MutationKind Kind,
    string NodeId,
    string PropertyName,
    string Value,
    MutationPreview Preview);

/// <summary>
/// The exactly two write capabilities exposed to the chat model. Every
/// model-invoked call is preview-only: it produces a <see cref="PendingMutation"/>
/// that a person must approve. <see cref="ApplyPendingAsync"/> is the only path
/// that confirms a change.
/// </summary>
public interface IGraphWriteTools
{
    [Description(
        "Proposes an update to one editable property (a Data value or the label) of a " +
        "knowledge-layer node such as a KnowledgeArticle or Resolution. Database-derived " +
        "nodes (Ticket, TicketDetail, Requester, Status, Day) are read-only and cannot be " +
        "changed with this tool. Calling this tool does not apply anything; it only produces " +
        "a preview that a human must approve.")]
    Task<MutationResult> UpdateNodeContent(
        [Description("The node id to edit, for example 'article:...' or 'resolution:...'.")] string nodeId,
        [Description("The property to change: 'label', or the name of a Data property.")] string propertyName,
        [Description("The new value for the property.")] string value);

    [Description(
        "Proposes a change to an existing Ticket's status or description. The valid statuses " +
        "are New, Open, Urgent, and Closed. This writes to the SQL database (the system of " +
        "record for tickets) and then mirrors the change into the graph. No other ticket field, " +
        "and no other database-derived node, can be changed. Calling this tool does not apply " +
        "anything; it only produces a preview that a human must approve.")]
    Task<MutationResult> UpdateTicket(
        [Description("The ticket node id, for example 'ticket:42'.")] string ticketId,
        [Description("The property to change: 'status' or 'description'.")] string propertyName,
        [Description("The new value: a valid status (New, Open, Urgent, Closed) or the new description text.")] string value);

    /// <summary>The proposal awaiting human approval, or null when none is pending.</summary>
    PendingMutation? Pending { get; }

    /// <summary>Discards any pending proposal without applying it.</summary>
    void ClearPending();

    /// <summary>Applies the pending proposal. The only place that confirms a mutation.</summary>
    Task<MutationResult> ApplyPendingAsync(CancellationToken ct = default);
}
