using System.ComponentModel;
using SyncfusionHelpDesk.Services.Graph;

namespace SyncfusionHelpDesk.Services.AI.GraphTools;

/// <summary>Discriminates which writer a pending mutation dispatches to.</summary>
public enum MutationKind { NodeContent, Ticket }

/// <summary>A proposed change awaiting human approval.</summary>
public sealed record PendingMutation(
    MutationKind Kind,
    string NodeId,
    string PropertyName,
    string Value,
    MutationPreview Preview);

/// <summary>
/// The two write capabilities exposed to the model. Both model-invoked methods
/// only ever produce a preview (<c>confirmed: false</c>); the approval boundary
/// is enforced by this class, and <see cref="ApplyPendingAsync"/> is the only
/// place that passes <c>confirmed: true</c>.
/// </summary>
public interface IGraphWriteTools
{
    [Description("Propose an update to a single Data property (or the label) of an editable knowledge-layer node such as a KnowledgeArticle or Resolution. Database-derived nodes (Ticket, TicketDetail, Requester, Status, Day) are read-only and cannot be changed with this tool. Calling this tool only produces a preview; a human must approve it before anything is written.")]
    Task<MutationResult> UpdateNodeContent(
        [Description("The exact id of the node to update.")] string nodeId,
        [Description("The Data property to change, or 'label' to change the display label.")] string propertyName,
        [Description("The new value.")] string value);

    [Description("Propose a change to an existing Ticket's status or description. Valid statuses are New, Open, Urgent, and Closed. This writes to the SQL database and then mirrors the change into the graph. Only status and description may change; all other Ticket fields and every other database-derived node are read-only. Calling this tool only produces a preview; a human must approve it before anything is written.")]
    Task<MutationResult> UpdateTicket(
        [Description("The ticket id, in the form 'ticket:<number>'.")] string ticketId,
        [Description("The field to change: 'status' or 'description'.")] string propertyName,
        [Description("The new value. For status, one of New, Open, Urgent, or Closed.")] string value);

    /// <summary>The proposal produced by the last write-tool call, if any.</summary>
    PendingMutation? Pending { get; }

    /// <summary>Discards any pending proposal without applying it.</summary>
    void ClearPending();

    /// <summary>Applies the pending proposal. The only path that confirms a mutation.</summary>
    Task<MutationResult> ApplyPendingAsync(CancellationToken ct = default);
}
