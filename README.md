# SyncfusionHelpDesk

![screenshot](https://github.com/user-attachments/assets/1af12cac-da3a-4f9d-8298-dfe0a2d9d9ea)

<img width="970" height="690" alt="image" src="https://github.com/user-attachments/assets/6f91eeb5-7dd6-42a8-a0b0-861903662326" />


## Covered in the Book:
[Blazor Succinctly](https://www.syncfusion.com/ebooks/blazor-succinctly)

### To Install

1) Create a Database on your SQL server, and run scripts in **!SQL directory**
2) Edit *appsettings.json* to set the database connection in the **DefaultConnection** property

### To Enable Syncfusion

1) Get an **API key** from [Syncfusion.com](https://support.syncfusion.com/kb/article/9795/how-to-get-community-license-and-install-it)
2) Open **appsettings.json**: 
- For **SYNCFUSION_APIKEY** enter your Syncfusion API key

### To Enable Emails

1) Get an **API key** from [app.sendgrid.com](https://app.sendgrid.com)
2) Open **appsettings.json**: 
- For **SENDGRID_APIKEY** enter your SendGrid API key 
- For **SenderEmail** enter your Email address 

### To Enable OpenAI

1) Open **appsettings.json**: 
- For **OpenAI/apiKey** enter your *Open AI* API key
- Optionally set **OpenAI/model** to the OpenAI model used by Smart Paste
  (default `gpt-4o-mini`)

> Note: the **OpenAI** section drives the Syncfusion Smart Components. The knowledge-graph
> assistant (below) uses a separate **AI** section. The two are independent.

## Knowledge Graph (AI)

This app includes a lightweight knowledge graph built from the help-desk database, plus
an AI assistant that answers relationship questions by calling read-only graph tools. It
follows Chapters 7-9 of *Lightweight Knowledge Graphs Succinctly*; see
[docs/knowledge-graph-implementation-plan.md](docs/knowledge-graph-implementation-plan.md)
for the full design.

**Pages**
- **/graph** — an interactive graph (rendered with Cytoscape.js): filter by node type,
  click a node to inspect it, and click **Rebuild Graph** to regenerate it from the
  current database.
- **/graphchat** — the *Help-Desk Graph Assistant*. Ask questions like "how many tickets
  has this email opened?" or "list the open tickets"; answers come from graph tool calls.

**Schema**
- Node types: `Ticket`, `TicketDetail`, `Requester`, `Status`, `Day` (built from the
  database), plus optional knowledge-layer nodes `KnowledgeArticle` and `Resolution`.
- Edge types: `REQUESTED_BY`, `HAS_DETAIL`, `HAS_STATUS`, `OCCURRED_ON`, plus
  `LINKED_TO`, `REFERENCES_ARTICLE`, `RESOLVED_BY` in the knowledge layer.
- The graph is stored under **App_Data/graph** as `graph.json`, `manifest.json`,
  `metadata.json`, and an `audit.log`.

### To Configure the Graph AI Assistant

1) Open **appsettings.json** and set the **AI** section (store real keys in user secrets,
   not in source control):
- Set **AI/ApiKey** to your OpenAI API key.
- Optionally set **AI/Model** to the OpenAI model to use (default `gpt-4o-mini`).

OpenAI is the only supported AI provider.

2) **Graph/OutputPath** sets where the graph files are written (default `App_Data/graph`);
   the folder is created at startup.

3) Run the app, sign in, open **/graph**, and click **Rebuild Graph** to build the graph
   from your tickets. Then open **/graphchat** to query it.

### Also See
* [SyncfusionHelpDesk - Blazor WebAssembly version](https://github.com/ADefWebserver/SyncfusionHelpDeskClient)
