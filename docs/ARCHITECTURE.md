# Architecture

mojoCAD is structured as a **hexagonal (ports & adapters)** application. The domain and the agent are pure managed code with no knowledge of AutoCAD; a single adapter project is the only code that ever touches the AutoCAD `Database`. This document describes each project, the ports/adapters contract, the change-set lifecycle, the threading model, and the provisional-handle mechanism.

## Projects at a glance

| Project | Target frameworks | References | Responsibility |
|---|---|---|---|
| `MojoCad.Core` | `net8.0`, `net48` | — | The domain. Change-set model, settings, DTOs, and the **ports** (interfaces) the adapters implement. Plus the pure-managed infrastructure (DPAPI key store, JSONL audit log, settings/conversation stores, standards lint). No AutoCAD, no WPF. |
| `MojoCad.Agent` | `net8.0`, `net48` | Core | The OpenRouter client and the agentic loop. The tool surface, system-prompt builder, SSE streaming parser and error mapping. Reaches the drawing only through the Core `IAcadBridge` port; produces a reviewable `ChangeSet`. No AutoCAD, no WPF — unit-testable off AutoCAD. |
| `MojoCad.Acad` | `net8.0-windows`, `net48` | Core | **The only project that references the AutoCAD managed assemblies and touches the `Database`.** Implements the three Core ports: read (`IAcadBridge`), transient preview (`IChangePreviewer`) and atomic commit (`IChangeApplier`). |
| `MojoCad.Ui` | `net8.0-windows`, `net48` | Core, `CommunityToolkit.Mvvm` | The WPF chat palette. MVVM view-models and views. Depends **only** on Core — never on the Agent, Acad or AutoCAD assemblies; it receives a fully-wired `MojoServices`. |
| `MojoCad.Plugin` | `net8.0-windows`, `net48` | Core, Agent, Acad, Ui | The AutoCAD entry point and **composition root**. The assembly the user NETLOADs. Wires concrete adapters into a `MojoServices` graph and hosts the WPF palette in a `PaletteSet`. Exposes the `MOJO` / `MOJOCHAT` commands. |

### Why this shape

- **The dependency rule.** Core and Agent have **zero** AutoCAD dependencies. Dependencies always point inward toward Core. The agent cannot reach AutoCAD types even by accident: the only seam is the `IAcadBridge` port (for reads) and the `ProposedOp`/`ChangeSet` model (for writes).
- **One adapter touches AutoCAD.** All Autodesk API usage is isolated in `MojoCad.Acad`. The agent and UI are testable without standing up AutoCAD.
- **The UI is decoupled from the agent.** The palette depends only on Core contracts, so the plugin can swap an adapter (or stub it in tests) without touching the UI.
- **One composition root.** `MojoCad.Plugin.PluginEntry` is the single place concrete types are wired together (`BuildServices`), producing the `MojoServices` container passed to the UI.

## Ports & adapters

`MojoServices` (in `MojoCad.Core/Composition`) is the bundle of ports handed from the composition root to the UI. Every member is a Core interface; the plugin supplies the concrete implementation.

| Port (Core interface) | Concrete adapter | Project | What it does |
|---|---|---|---|
| `IAgentService` | `AgentService` | Agent | Runs one user turn: reason, read the drawing through tools, stage a change set, stream progress to an `IAgentObserver`. Never applies anything. |
| `IOpenRouterAccount` | `OpenRouterClient` | Agent | Validate a pasted key against `GET /api/v1/key`; list tool-capable models from `GET /api/v1/models`. (The same class implements the internal chat client.) |
| `IAcadBridge` | `DrawingReader` | Acad | Read-only drawing access: summary, entity queries, properties, CAD-computed measurement, handle/layer/block existence, current selection, zoom. |
| `IChangePreviewer` | `TransientPreview` | Acad | Render a change set as transient graphics + highlighting — **no DB writes**. Green/amber/red ghosts; visibility toggling; flash/zoom; clear. |
| `IChangeApplier` | `ChangeApplier` | Acad | Commit the accepted subset atomically as one named undo group; undo the last applied change set. |
| `ISecureStore` | `DpapiSecureStore` | Core | DPAPI-encrypted OpenRouter key storage (CurrentUser scope). |
| `IAuditLog` | `JsonlAuditLog` | Core | Append-only, per-drawing audit trail of applied changes. |
| `IStandardsLint` | `StandardsLint` | Core | Pre-apply linting against the active standards (BYLAYER discipline, layer naming, life-safety geometry sanity). |
| `IConversationStore` | `JsonFileConversationStore` | Core | Persistence for chat transcripts across palette close / AutoCAD restart. |
| `SettingsStore` | (concrete class) | Core | Load/save `MojoSettings` to `%APPDATA%\mojoCAD\config.json` (atomic temp+move). The key is **not** in this file. |

The UI talks to the streaming agent through the `IAgentObserver` callbacks: `OnStatus`, `OnAssistantTextDelta`/`OnAssistantText`, `OnPlan`, `OnToolActivity`, `OnQuestion`, `OnChangeSetReady`, `OnError`, `OnTurnComplete`. Every callback fires on a background thread; the chat view-model marshals to the WPF dispatcher.

## The change-set lifecycle

A `ChangeSet` is the unit of agreement between the AI and the engineer. Every turn that wants to alter the drawing finalises exactly one of them.

```
   Agent write tool                ChangeSetBuilder            emit_changeset            ChangeApplier
   (e.g. create_wall)                 (per turn)               (control tool)          (Acad adapter)
        │                                 │                          │                      │
        │  build a ProposedOp             │                          │                      │
        ├────────────────────────────────►│  Add(op):                │                      │
        │                                 │   • assign OpId "op-N"    │                      │
        │                                 │   • assign provisional    │                      │
        │                                 │     handle "@op-N"        │                      │
        │  (more write tools…)            │                          │                      │
        │                                 │  Finalize(summary) ──────►│                      │
        │                                 │                          ├─ OnChangeSetReady ──► UI preview + review card
        │                                 │                          │                      │
        │                                 │             engineer ticks the ops to accept     │
        │                                 │                          │                      │
        │                                 │                          │   ApplyAsync(accepted subset, undoLabel)
        │                                 │                          ├─────────────────────►│  one locked transaction,
        │                                 │                          │                      │  one undo group → ApplyResult
```

1. **`ProposedOp`** — a single, fully-described, AutoCAD-free operation (the abstract base in `MojoCad.Core/Changes`). Concrete kinds mirror the WRITE tool surface: `CreatePolylineOp`, `CreateCircleOp`, `MoveOp`, `EraseOp`, `PropertyChangeOp`, etc. Each carries everything the Acad adapter needs to *preview* and to *commit*: layer, optional property overrides (discouraged), plain-language description, severity, target handles, and free-form `Meta` (e.g. a wall's centerline so a later opening can be positioned in it). Categories drive the colour legend: `Additive` (green), `Modify` (amber), `Erase` (red), `Organizational` (slate, no geometry).

2. **`ChangeSetBuilder`** (Agent) — accumulates the ops staged during one turn. It assigns each op a stable id (`op-1`, `op-2`, …) and, for ops that produce geometry, a **provisional handle** (`@op-N`) the model can reference in later ops the same turn.

3. **`emit_changeset`** (control tool) — the agent's only path to the drawing. It finalises the builder into a `ChangeSet` with a plain-language summary and raises `OnChangeSetReady`. If nothing was staged, the tool returns a `NOTHING_STAGED` error so the model self-corrects.

4. **`ChangeSet`** — `Id`, `Summary`, an ordered `Ops` list, and an aggregate `State`. `ComputeStats()` returns trustworthy counts derived from the ops (never a model-supplied number). `AcceptAll(includeErase = false)` marks non-blocking, non-erase ops accepted by default.

5. **Preview** — `IChangePreviewer.ShowAsync` draws the change set as transients: additive → green, modify → amber ghost of the new state, erase → red highlight on the doomed entities. As the engineer ticks/unticks rows, `UpdateVisibilityAsync` restricts the preview. None of this writes to the database, so **Reject is free**.

6. **Accepted subset** — each `ProposedOp.State` becomes `Accepted` or `Rejected`. `ChangeSet.AcceptedOps` is the subset that will commit.

7. **`ChangeApplier`** (atomic transaction) — `ApplyAsync(changeSet, undoLabel)` commits the accepted subset inside **one** document-locked transaction under one undo group. If any op throws, the transaction is abandoned (full rollback): every accepted op is marked `Failed`, the applied list is cleared, and the `ApplyResult` reports failure. On success the result lists the applied op ids and the undo label, and each op's `ResultHandles` are filled with the real AutoCAD handles.

`StandardsLint` runs **before** apply and can attach `Blocking` findings (e.g. non-positive radius, polyline with <2 vertices, per-entity property overrides). A `Blocking` op cannot be accepted until the underlying issue is resolved.

## Threading model

AutoCAD's `Database` and document operations are single-threaded and must run on AutoCAD's main thread; the agent loop runs on a background thread so the UI stays responsive. mojoCAD bridges the two with `AcadContext` (in `MojoCad.Acad/Interop`).

- **Capture the main thread at load.** `PluginEntry.Initialize` runs on AutoCAD's main thread when the assembly loads and calls `AcadContext.Initialize()`, which captures the main-thread `SynchronizationContext` and thread id.
- **The agent loop runs in the background.** `AgentService.RunTurnAsync` streams the model and executes tool calls off the UI thread. Tool calls are executed **serially** (`parallel_tool_calls` is off) so ordered ops (later ops depending on earlier ones) stay deterministic.
- **Reads marshal to the main thread.** Every `IAcadBridge` read posts onto the captured context via `AcadContext.InvokeAsync` / `ReadAsync`, opening a short read transaction on the active document — so background callers never touch the `Database` off-thread.
- **Commits use command context + a document lock + a single transaction.** `ChangeApplier.ApplyAsync` marshals onto the main thread via `DocumentManager.ExecuteInCommandContextAsync`, takes a `doc.LockDocument()`, and opens **one** `StartTransaction()`. Every accepted op is applied inside that transaction; `Commit()` makes it a single Ctrl+Z step. A thrown exception disposes the transaction without committing — strict all-or-nothing. `UndoLastAsync` sends `_.U` to undo the most recent applied change set.
- **Transient preview is main-thread too.** `TransientPreview` uses `TransientManager` (no DB writes) and updates transients on the main thread; clearing them removes the highlight/ghosts with no side effects.

## Provisional handles

The model often needs to reference geometry it just created *before anything is applied* — e.g. drawing a wall and then placing a door in that wall in the same turn. AutoCAD handles don't exist yet at stage time, so mojoCAD assigns **provisional handles**.

- When `ChangeSetBuilder.Add` stages a geometry-producing op (additive creates, copies, arrays, mirror-with-keep-original), it appends a provisional handle of the form `@op-N` to the op's `ResultHandles` and records it in `ProvisionalHandles`.
- Tool results returned to the model include this provisional handle (e.g. `draw_wall` returns `wall_id: "@op-3"`). The model passes it back into later edit/domain tools the same turn.
- Edit tools validate handle arguments against both existing drawing handles (via `IAcadBridge.FilterExistingHandlesAsync`) and the turn's provisional set, so a reference to staged-but-not-applied geometry is accepted while a typo'd handle is rejected with a self-correction hint.
- At commit time, `ChangeApplier` resolves provisional references: as each op is applied it records its created `ObjectId`s under both the op id and its `@op-N` provisional handle. Later ops in the same apply that reference `@op-N` resolve to the freshly created entities; real hex handles resolve normally via the database. After apply, each op's `ResultHandles` are rewritten to the real persistent handles.

This lets a single turn compose multi-step constructions (wall → opening, boundary → hatch, branch line → connected sprinkler heads) that all preview and commit together as one reviewable, atomic change set.
