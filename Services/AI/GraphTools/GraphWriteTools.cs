using SyncfusionHelpDesk.Services.Graph;

namespace SyncfusionHelpDesk.Services.AI.GraphTools;

/// <summary>
/// Enforces the approval boundary in code. Every model-invoked method hard-codes
/// <c>confirmed: false</c> and stashes the preview in <see cref="Pending"/>. The
/// strongest thing a confused or adversarial model can do is leave a proposal
/// sitting in <see cref="Pending"/>; only <see cref="ApplyPendingAsync"/> ever
/// confirms it.
/// </summary>
public sealed class GraphWriteTools : IGraphWriteTools
{
    private readonly GraphMutationService _service;

    public GraphWriteTools(GraphMutationService service) => _service = service;

    public PendingMutation? Pending { get; private set; }

    public void ClearPending() => Pending = null;

    public async Task<MutationResult> UpdateNodeContent(
        string nodeId, string propertyName, string value)
    {
        var result = await _service.UpdateNodeContent(nodeId, propertyName, value, confirmed: false);
        if (result.Status == MutationStatus.PreviewOnly && result.Preview is not null)
        {
            Pending = new PendingMutation(
                MutationKind.NodeContent, nodeId, propertyName, value, result.Preview);
        }

        return result;
    }

    public async Task<MutationResult> UpdateTicket(
        string ticketId, string propertyName, string value)
    {
        var result = await _service.UpdateTicket(ticketId, propertyName, value, confirmed: false);
        if (result.Status == MutationStatus.PreviewOnly && result.Preview is not null)
        {
            Pending = new PendingMutation(
                MutationKind.Ticket, ticketId, propertyName, value, result.Preview);
        }

        return result;
    }

    public async Task<MutationResult> ApplyPendingAsync(CancellationToken ct = default)
    {
        var pending = Pending;
        if (pending is null)
        {
            return MutationResult.Rejected("There is no pending mutation to apply.");
        }

        var result = pending.Kind switch
        {
            MutationKind.Ticket => await _service.UpdateTicket(
                pending.NodeId, pending.PropertyName, pending.Value, confirmed: true, ct),
            _ => await _service.UpdateNodeContent(
                pending.NodeId, pending.PropertyName, pending.Value, confirmed: true, ct),
        };

        Pending = null;
        return result;
    }
}
