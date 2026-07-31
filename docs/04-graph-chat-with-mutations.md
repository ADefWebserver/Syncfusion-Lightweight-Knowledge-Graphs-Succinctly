# Graph Chat with Controlled Mutations

## 1. Overview

This plan extends the read-only Graph Chat assistant with a controlled write
path. The assistant may now **propose** a change, but nothing is applied until a
person reviews a preview and clicks Approve, and every applied change is
recorded in an audit log.

At this stage the application already has a file-based graph under
`App_Data/graph`, a `GraphStore` that keeps it in memory, and a `/graphchat`
page that answers questions through deterministic `Microsoft.Extensions.AI`
tools.

## 2. Two Kinds of Change

The two write paths differ in where the truth lives.

| | Ticket edit | Knowledge-layer edit |
| --- | --- | --- |
| System of record | SQL Server | `graph.json` |
| Tool | `UpdateTicket` | `UpdateNodeContent` |
| Targets | `Ticket` nodes only | `KnowledgeArticle`, `Resolution` |
| Editable fields | `status`, `description` | any `Data` property, or `label` |
| Write order | SQL first, then mirror into the graph | graph only |

**Ticket edits.** The SQL database remains the system of record for tickets, so
the change is written to SQL first and only then mirrored into the graph.
Exactly two ticket fields may change: `status` and `description`. Nothing else
about a `Ticket` may be edited, and `TicketDetail`, `Requester`, `Status`, and
`Day` nodes stay strictly read-only. No database-derived node may be created or
deleted through this feature.

**Knowledge-layer edits.** An optional graph-native layer has no database behind
it at all: `KnowledgeArticle` and `Resolution` nodes, plus `LINKED_TO`
(Ticket to Ticket), `REFERENCES_ARTICLE` (Ticket to KnowledgeArticle), and
`RESOLVED_BY` (Ticket to Resolution) edges. These exist only in `graph.json`.

## 3. System Structure

```mermaid
flowchart TD
    Model[LLM via IChatClient]
    Read[GraphChatTools read only]
    Write[GraphWriteTools two tools]
    Pending[PendingMutation with Kind]
    Dialog[MutationApproval SfDialog]
    Service[GraphMutationService sole writer]
    Sql[(SQL Server help desk tables)]
    Files[graph.json manifest.json audit.log]
    Store[GraphStore]

    Model --> Read
    Read --> Store
    Model --> Write
    Write -->|confirmed false| Service
    Service -->|preview| Pending
    Pending --> Dialog
    Dialog -->|approve| Write
    Write -->|confirmed true| Service
    Service --> Sql
    Service --> Files
    Service --> Store
```

## 4. Two-Phase Mutation Semantics

Every mutation uses the same contract:
`MutationResult Name(args, bool confirmed = false)`.

- **confirmed = false (preview only):** the operation writes nothing, to SQL or
  to disk. It returns a `MutationResult` whose `Preview` contains a
  plain-language summary, the files that would be affected, and any validation
  warnings.
- **confirmed = true (apply):** the operation applies the change, rebuilds the
  graph indexes, updates the manifest, appends an audit entry, and returns the
  resulting state.

## 5. Mutation Models

`Services/Graph/MutationModels.cs`. These are **records, not mutable classes**,
so a preview cannot be altered between the moment it is shown to a person and
the moment the change is applied.

```csharp
public enum MutationStatus { Rejected, PreviewOnly, Applied }

public sealed record MutationPreview(
    string Summary,
    IReadOnlyList<string> AffectedFiles,
    IReadOnlyList<string> Warnings);

public sealed record MutationResult(
    MutationStatus Status,
    MutationPreview? Preview,
    IReadOnlyList<string> Errors)
{
    public static MutationResult Rejected(params string[] errors) => ...;
    public static MutationResult PreviewOnly(MutationPreview preview) => ...;
    public static MutationResult Applied(MutationPreview preview) => ...;
}
```

## 6. Supported Operations and Validation Rules

| Mutation | Effect | Validation rules |
| --- | --- | --- |
| UpdateTicket | Change a ticket's `status` or `description` in SQL, then mirror into the graph | Node must exist and be a `Ticket`; property must be in `EditableTicketProperties`; a status must match `HelpDeskStatusData.Statuses` case-insensitively; a description must not be blank; the id must parse as `ticket:<number>` |
| UpdateNodeContent | Update one `Data` property (or the label) of an editable node | Target must not be in `ReadOnlyNodeTypes`; never creates or deletes a node, never alters an edge |
| LinkTickets | Add a LINKED_TO edge Ticket to Ticket | Reject self-links; reject duplicates in either direction; both endpoints must be existing `Ticket` nodes |
| AddKnowledgeArticle | Create a KnowledgeArticle node with id `article:<guid>` | Title is required |
| ReferenceArticle | Add a REFERENCES_ARTICLE edge Ticket to KnowledgeArticle | Reject duplicates; endpoints must exist with the expected types |
| RecordResolution | Create a Resolution node with id `resolution:<guid>` and its RESOLVED_BY edge | Ticket endpoint must exist |
| DeleteArticle | Remove an article and its incoming edges | Reject while REFERENCES_ARTICLE edges still point to it, unless `cascade` is true; report the edge count as a preview warning |

## 7. GraphMutationService (sole writer)

`Services/Graph/GraphMutationService.cs` is the **only** component allowed to
write graph files.

Constructor dependencies: `GraphStore`, `IOptions<GraphOptions>`,
`IWebHostEnvironment`, and `IDbContextFactory<SyncfusionHelpDeskContext>`.

Private state:

```csharp
private const string AuditFileName = "audit.log";

// Static so writes serialize process-wide even though the service is scoped
// and a new instance exists per request.
private static readonly SemaphoreSlim WriteGate = new(1, 1);

private static readonly HashSet<string> ReadOnlyNodeTypes = new(StringComparer.Ordinal)
{
    "Ticket", "TicketDetail", "Requester", "Status", "Day",
};

private static readonly HashSet<string> EditableTicketProperties = new(StringComparer.OrdinalIgnoreCase)
{
    "status", "description",
};

private static readonly string[] PreviewFiles =
    { GraphFile.GraphFileName, GraphFile.ManifestFileName, AuditFileName };
```

### 7.1 The shape every mutation follows

The ordering below **is** the safety model. Implement every mutation this way.

1. Put all validation in a local function
   `MutationResult? Validate(GraphDocument doc)` that returns `null` when the
   request is acceptable and a rejection otherwise.
2. Load the document from disk, call `Validate`, and return early on rejection.
   Do **not** take the lock for this; a preview only reads, so previews never
   contend with one another.
3. Build the `MutationPreview` and return `MutationResult.PreviewOnly` when
   `confirmed` is false. Nothing has been written, to SQL or to disk.
4. When `confirmed` is true, take the lock, **re-load** the document from disk,
   and call `Validate` a second time.
5. Apply the change, then call `CommitAsync`.
6. Release the lock in a `finally` block.

> **Why validate twice.** Minutes can pass between a preview appearing on screen
> and a person clicking Approve. In that window the ticket may have been closed
> in the help-desk UI, or the node may have been edited by another request.
> Re-validating under the lock means an approval can only ever apply to a state
> that still holds.

Always work on a `GraphDocument` freshly loaded from disk rather than the one
held in `GraphStore`, so a rejected or preview-only call can never leave the
in-memory graph half-changed.

### 7.2 CommitAsync

The shared tail of every applied mutation:

1. Write `graph.json` atomically (temp file, then `File.Move` with overwrite).
2. Write `manifest.json` with the new `lastMutationUtc`.
3. Call `GraphStore.ReloadAsync` to rebuild the in-memory indexes.
4. Append one tab-separated line to `audit.log`: the UTC timestamp in round-trip
   format, then a detail string such as
   `UpdateTicket id=ticket:42 property=status`.

### 7.3 UpdateTicket

`UpdateTicket(ticketId, propertyName, value, confirmed, ct)` is the only
mutation that leaves the graph files.

Its `Validate` function checks, in order:

1. A node with `ticketId` exists.
2. Its `Type` is `Ticket`.
3. `propertyName` is in `EditableTicketProperties`.
4. If the property is `status`, the value matches an entry in
   `HelpDeskStatusData.Statuses` compared case-insensitively. The rejection
   message must list the valid statuses.
5. If the property is `description`, the value is not blank.
6. The id parses as `ticket:<number>`, so the database key can be recovered.

**Capture the canonical status.** The status check should capture the `ID` from
the matching `HelpDeskStatusData` entry and write *that*, not the string the
model sent. A model asking for `closed` results in `Closed` in the database, so
the graph's `status:` ids stay consistent with the ones a full rebuild would
produce.

After confirmation:

1. Load the `HelpDeskTicket` by the recovered key, set `TicketStatus` or
   `TicketDescription`, and call `SaveChangesAsync`. If the row has since
   disappeared, reject.
2. Mirror the change into the loaded `GraphDocument` **in place**.
3. Call `CommitAsync`.

```mermaid
flowchart TD
    Start[UpdateTicket confirmed true] --> Lock[Take write gate]
    Lock --> Reload[Re-load document and re-validate]
    Reload --> Sql[Load HelpDeskTicket and SaveChangesAsync]
    Sql --> Which{Property}
    Which -- description --> Label[Set node label to first 60 chars]
    Which -- status --> Data[Set node status to canonical value]
    Data --> Repoint[RepointStatusEdge]
    Label --> Commit[CommitAsync]
    Repoint --> Commit
    Commit --> Files[Write graph json and manifest json]
    Files --> Store[Reload GraphStore]
    Store --> Audit[Append audit log line]
```

### 7.4 Why mirror instead of rebuild

Do **not** call `HelpDeskGraphBuilder.BuildAsync` to pick up a ticket edit. A
full rebuild derives the graph from the database alone, so it would discard the
entire knowledge layer: articles, resolutions, and links exist only in
`graph.json` and have no database rows behind them. Mirroring the single changed
field in place keeps both layers intact.

For a description change, set the node label to the first 60 characters of the
new text, matching `HelpDeskGraphBuilder`'s truncation rule, so a mirrored edit
and a rebuilt node produce the same label.

### 7.5 RepointStatusEdge

A private helper that moves a ticket's `HAS_STATUS` edge:

1. Compute the target id as `status:<newStatus lowercased>`.
2. If no node with that id exists, create the `Status` node. This happens when
   no ticket currently holds that status.
3. Remove every `HAS_STATUS` edge whose `From` is the ticket.
4. Add the new `HAS_STATUS` edge from the ticket to the status node.

Known limitation to state in the plan: when the last ticket moves off a status,
the now-unreferenced `Status` node is left behind. It is harmless, and the next
full rebuild removes it, because the builder only emits statuses it finds on
tickets.

## 8. The Write Tools

`Services/AI/GraphTools/IGraphWriteTools.cs` and `GraphWriteTools.cs` expose
exactly **two** write capabilities to the model:

- `UpdateNodeContent(nodeId, propertyName, value)` for the knowledge layer.
- `UpdateTicket(ticketId, propertyName, value)` for tickets.

Both return `Task<MutationResult>`, **not** a formatted string.
`Microsoft.Extensions.AI` serializes the result, so the model receives the
status and any error messages as structured data that cannot drift out of sync
with the service.

The service supports the full mutation domain, but the model never sees
`LinkTickets`, `AddKnowledgeArticle`, `ReferenceArticle`, `RecordResolution`, or
`DeleteArticle`. From where the model sits, it cannot create or delete a node,
and it cannot add, change, or remove an edge. The structure of the graph is
fixed; only contents change.

Each `[Description]` must state what the tool can change, name the valid
statuses for `UpdateTicket`, say that `UpdateTicket` writes to the SQL database
and mirrors into the graph, say that database-derived nodes are otherwise
read-only, and say that calling the tool only produces a preview that a human
must approve.

### 8.1 The approval boundary is enforced by the class

Both model-invoked methods hard-code `confirmed: false` and stash the result:

```csharp
public enum MutationKind { NodeContent, Ticket }

public sealed record PendingMutation(
    MutationKind Kind,
    string NodeId,
    string PropertyName,
    string Value,
    MutationPreview Preview);
```

Set `Pending` **only when the returned status is `PreviewOnly`**, so a rejected
request can never leave something approvable behind.

The `Kind` discriminator exists because there are now two writers to dispatch
to. `ApplyPendingAsync` is the **only** place in the codebase that passes
`confirmed: true`, and it switches on `Kind`:

```csharp
var result = pending.Kind switch
{
    MutationKind.Ticket => await _service.UpdateTicket(
        pending.NodeId, pending.PropertyName, pending.Value, confirmed: true, ct),
    _ => await _service.UpdateNodeContent(
        pending.NodeId, pending.PropertyName, pending.Value, confirmed: true, ct),
};
```

The interface also exposes `Pending` and `ClearPending()`. The strongest thing a
confused or adversarially prompted model can do is leave a proposal sitting in
`Pending`.

### 8.2 Tool registration

Add a `GraphToolRegistration.CreateTools(read, write)` overload that appends
both write tools to the eight read-only traversal tools.

> **CRITICAL.** `GraphChat.razor` must call the **two-argument** overload when
> it builds `ChatOptions.Tools`:
>
> ```csharp
> Tools = GraphToolRegistration.CreateTools(GraphTools, GraphWriteTools),
> ```
>
> Leaving the single-argument read-only call in place is a **silent failure, not
> a compile error**. The service is still registered, the page still injects
> `IGraphWriteTools`, the approval dialog is still on the page, and the system
> prompt still tells the model it has two write tools — but the model never
> receives them. `Pending` is never populated, the dialog never appears, and the
> model, having been told it can update tickets, will either refuse for the
> wrong reason or announce a change that never happened.

## 9. System Prompt Changes

Update `GraphSystemPrompt` to describe the optional knowledge layer and both
write tools. Give the write tools their own section, and the rules governing
them a **separate** `Write rules` section rather than folding them into the tool
descriptions. A tool description is read while the model is choosing what to
call; a rule is read as policy.

The rules must say:

- The model **MAY** update an existing `Ticket`'s status or description with
  `UpdateTicket`, but only when the user has clearly requested a change.
- It must **NEVER** claim to create or delete `Ticket`, `TicketDetail`,
  `Requester`, `Status`, or `Day` nodes, and cannot add, change, or remove edges.
- Calling a write tool **does not apply anything**. It only produces a preview
  that a person must approve, so the model must report the tool result exactly
  and must not claim a change was applied.

That last rule is the one that earns its place. Without it, a model that sees a
successful-looking `PreviewOnly` result will tell the user their ticket is
closed, when nothing has been written and a dialog is waiting for them.

## 10. The Approval Dialog

`Components/Shared/MutationApproval.razor` is a modal Syncfusion `SfDialog` that
displays the `MutationPreview` summary, affected files, and warnings, with
Approve and Reject buttons. Parameters: `Visible`, `VisibleChanged`, `Preview`,
`OnApprove`, `OnReject`.

Dismissing the dialog with the close icon is treated as a **rejection**, so no
file can change without an explicit approval. The component knows nothing about
graphs, tools, or files; it takes a preview and two callbacks and nothing more.

## 11. GraphChat.razor Wiring

- Call `ClearPending()` at the start of each turn, so a stale proposal from an
  earlier turn cannot be approved by accident.
- Build `ChatOptions.Tools` with the **two-argument** overload (section 8.2).
- After the response returns and has been converted to HTML, check
  `IGraphWriteTools.Pending`. When it is populated, set the preview and show the
  approval dialog.
- Call `ApplyPendingAsync()` only from the approve callback.
- Call `ClearPending()` from the reject callback.

```mermaid
flowchart TD
    Turn[New prompt] --> Clear[ClearPending]
    Clear --> Ask[GetResponseAsync with read and write tools]
    Ask --> Html[Convert markdown to HTML]
    Html --> Check{Pending populated}
    Check -- No --> Answer[Show answer only]
    Check -- Yes --> Show[Show MutationApproval dialog]
    Show --> Choice{User choice}
    Choice -- Approve --> Apply[ApplyPendingAsync with confirmed true]
    Choice -- Reject --> Discard[ClearPending and write nothing]
    Apply --> Done[SQL and files updated and audited]
```

## 12. Program.cs Wiring

```csharp
builder.Services.AddScoped<GraphMutationService>();
builder.Services.AddScoped<IGraphWriteTools, GraphWriteTools>();
```

## 13. Acceptance Criteria

- A preview call (`confirmed == false`) writes nothing to disk and nothing to
  SQL.
- Asking the assistant to change a ticket **produces an approval dialog**, which
  proves the write tools were actually registered with the model.
- No change is applied until the human clicks Approve.
- Rejecting, or closing the dialog with the close icon, leaves every file and
  the database unchanged.
- Updating a ticket's status changes `TicketStatus` in SQL, updates the node's
  `status` value, and repoints the `HAS_STATUS` edge.
- A status supplied in the wrong case is stored in its canonical form.
- Updating a ticket's description changes `TicketDescription` in SQL and
  re-truncates the node label to 60 characters.
- An invalid status is rejected with a message naming the valid statuses.
- An attempt to edit a `TicketDetail`, `Requester`, `Status`, or `Day` node is
  rejected.
- An applied ticket edit does not discard existing knowledge-layer nodes or
  edges.
- Every applied mutation writes `graph.json` and `manifest.json` atomically and
  appends exactly one line to `audit.log`.
