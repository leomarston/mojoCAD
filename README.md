# mojoCAD

**Cursor for AutoCAD** — a chat copilot that lives inside AutoCAD and proposes precise, *reviewable* CAD edits.

mojoCAD is a dockable WPF chat palette hosted in AutoCAD, backed by an [OpenRouter](https://openrouter.ai) LLM agent. You describe what you want in plain language; the agent grounds itself in the live drawing, plans, and **stages** a change set of exact geometric operations. Nothing is ever written to your drawing until you read the staged changes — shown as colour-coded transient ghosts right on the canvas — and explicitly accept them. The whole accepted batch applies as a single atomic, named undo step. It is built safety-first for architectural, MEP and fire/life-safety work: the agent has no auto-apply mode by design, never guesses code parameters, and surfaces deletions in red where you must opt in. You remain the engineer of record.

---

## Screenshots

> Screenshots and the palette walkthrough live in [`assets/`](assets/). Drop captures of the chat palette, a colour-coded preview, the review card, and the Settings/model-picker screen there and reference them from this section. (No images are bundled in this repository yet.)

---

## Key features

- **Chat palette inside AutoCAD.** A dockable `PaletteSet` (`MOJO` / `MOJOCHAT`) with a streaming chat UI — assistant prose, collapsible tool-activity rows, plan cards, question cards and review cards.
- **Transient-graphics preview.** Staged changes are drawn on the canvas as AutoCAD transients with **no database writes**: green = new geometry, amber = the ghost of a modified entity's new state, red = doomed (erased) entities. Because nothing is committed, **Reject is free** — the drawing is never dirtied, no reactors fire, autosave is untouched.
- **Atomic accept / reject = one Ctrl+Z.** The accepted subset commits inside a single document-locked transaction under one named undo group. It is strictly all-or-nothing: if any operation fails, the entire apply rolls back, so you never get a half-applied life-safety change.
- **Plan-before-act.** The agent presents a short numbered plan before it stages geometry. For Fire & Life-Safety work (or whenever you enable it) the plan must be **approved** before the agent may proceed.
- **Ask-clarification.** When a request is ambiguous — and *mandatorily* for any unspecified life-safety parameter — the agent stops and asks rather than guessing.
- **OpenRouter model picker.** Pick any tool-capable model; the live catalogue is fetched from OpenRouter's `/models`. Ordered fallback models give automatic cross-vendor failover.
- **BYLAYER / standards awareness.** The agent is templated with your discipline preset, layer standard (AIA/NCS, BS 1192, ISO 13567, or custom), code references and annotation scale. It keeps colour/linetype/lineweight BYLAYER, and a pre-apply lint nudges toward your CAD standard.
- **Audit log.** Every applied change set is recorded to an append-only, per-drawing audit trail (what was applied, when, by which model) — never the API key or raw chain-of-thought.
- **DPAPI-encrypted key.** Your OpenRouter API key is encrypted with Windows DPAPI (`ProtectedData`, CurrentUser scope) and never written in clear text to disk, logs, the drawing or the repo.

---

## Architecture overview

mojoCAD follows a **hexagonal (ports & adapters)** layering. The domain and the agent know nothing about AutoCAD; a single adapter project is the only code that touches the AutoCAD `Database`.

```
                ┌─────────────────────────────────────────────┐
                │              MojoCad.Plugin                  │
                │   composition root · NETLOAD target ·        │
                │   PaletteSet host · MOJO / MOJOCHAT          │
                └───────────────┬──────────────┬──────────────┘
                                │ wires         │ hosts
              ┌─────────────────┴───┐    ┌──────┴───────────────┐
              │   MojoCad.Agent     │    │     MojoCad.Ui       │
              │ OpenRouter client + │    │  WPF chat palette    │
              │ agentic loop +      │    │  (MVVM)              │
              │ tool surface        │    │                      │
              │  (NO AutoCAD)       │    │  (NO AutoCAD/Agent)  │
              └─────────┬───────────┘    └──────────┬───────────┘
                        │ depends on                │ depends on
                        ▼                           ▼
                ┌─────────────────────────────────────────────┐
                │                MojoCad.Core                  │
                │  domain model (ChangeSet/ProposedOp) ·       │
                │  settings · DTOs · PORTS (interfaces)        │
                │            (NO AutoCAD, NO WPF)              │
                └───────────────────────┬─────────────────────┘
                                        │ implemented by
                                        ▼
                ┌─────────────────────────────────────────────┐
                │               MojoCad.Acad                   │
                │  THE ONLY AutoCAD-touching adapter:          │
                │  IAcadBridge (read) · IChangePreviewer       │
                │  (transient preview) · IChangeApplier        │
                │  (atomic commit)                             │
                └─────────────────────────────────────────────┘
```

**The dependency rule:** `MojoCad.Core` and `MojoCad.Agent` have **zero** AutoCAD dependencies. The agent reaches the drawing only through the Core `IAcadBridge` port and affects it only by producing a reviewable `ChangeSet`. The UI depends only on Core. The plugin (the composition root) is the single place where concrete adapters are wired into a `MojoServices` container. This keeps the domain, agent and UI unit-testable away from AutoCAD, and means swapping an adapter never touches the layers above it.

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the full project-by-project breakdown.

---

## The safety model

mojoCAD is designed so that the AI can never silently change your drawing. The core invariant:

> **The agent's only path to the drawing is `emit_changeset` → human review → atomic apply.**

- **No auto-apply mode exists, by design.** Write tools stage `ProposedOp`s into a pending change set; they never touch the AutoCAD `Database`. The agent must finalise everything with the `emit_changeset` control tool, which hands the engineer a reviewable change set.
- **You review, you decide.** Counts shown on the review card are computed from the operations themselves — never any model-supplied number — so the AI cannot misrepresent what it is about to do.
- **Deletions are never auto-accepted.** Erase operations are surfaced in red, default to a `Warning` severity, and are excluded from "accept all" unless you explicitly opt in.
- **Life-safety parameters require `ask_clarification`.** The agent is instructed never to guess a fire-resistance rating, egress/corridor width, occupant load, sprinkler spacing/coverage or travel distance. If it isn't given, it must ask.
- **Plan approval gate.** Fire & Life-Safety projects (and any project where you enable it) require the agent to present a plan and stop for approval before staging.
- **Atomic, reversible apply.** The accepted subset commits as one named undo group; on any failure the whole thing rolls back. An inline "Undo" affordance reverses the last applied change set.

See [`docs/SAFETY.md`](docs/SAFETY.md) for the complete safety design.

---

## Requirements

| | |
|---|---|
| **AutoCAD** | 2025+ (runs on .NET 8) **or** 2021–2024 (runs on .NET Framework 4.8) |
| **OS** | Windows (x64) |
| **API access** | An [OpenRouter](https://openrouter.ai) account and API key with credits |
| **Build** | .NET SDK with the `net8.0-windows` and `net48` targets; the `AutoCAD.NET` NuGet package (compile-only) |

mojoCAD ships a single multi-targeted codebase; the managed ObjectARX API surface it uses is identical across both AutoCAD runtimes. The plugin assemblies must be **x64**.

---

## Quick start

1. **Build** the solution (see [`docs/BUILD.md`](docs/BUILD.md) for prerequisites and the AutoCAD package versions):
   ```
   dotnet build mojoCAD.sln -c Release
   ```
   Build the target framework that matches your AutoCAD release (`net8.0-windows` for 2025+, `net48` for 2021–2024).

2. **Load the plugin.** In AutoCAD, run `NETLOAD` and select `MojoCad.Plugin.dll` from the build output.

3. **Open the palette.** Type `MOJO` (toggle) or `MOJOCHAT` (show) at the command line. The dockable chat palette appears.

4. **Add your key.** Open **Settings** in the palette and paste your OpenRouter API key. It is validated against OpenRouter and stored encrypted with DPAPI — it never lives in the config file or the drawing.

5. **Chat.** Describe what you want ("draw a 200mm wall along these points and add a 900mm door 1.2m from the start"). Read the plan, watch the staged changes preview on-canvas in green/amber/red, then **accept** the operations you want. They apply as one Ctrl+Z step.

---

## Models

The default and recommended models (the live list is fetched at runtime from OpenRouter's `/models`, filtered to tool-capable models):

| Model id | Label | Notes |
|---|---|---|
| `anthropic/claude-opus-4.8` | Claude Opus 4.8 | **Default.** Strongest tool-use & instruction-following; default for life-safety work. |
| `anthropic/claude-sonnet-4.6` | Claude Sonnet 4.6 | Near-Opus quality at lower cost; good default for everyday drafting. |
| `openai/gpt-5.5` | GPT-5.5 | Rock-solid strict tool calling; primary cross-vendor fallback. |
| `google/gemini-3.1-pro-preview` | Gemini 3.1 Pro | Largest context, cheapest flagship; use with `require_parameters`. |

The primary model plus an ordered list of fallbacks are sent to OpenRouter so the router can fail over automatically. By default mojoCAD sets the provider `data_collection` policy to **deny**, so prompts containing your proprietary CAD data are not routed to providers that log or train on them.

---

## Documentation

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — project responsibilities, the ports/adapters table, the change-set lifecycle, the threading model, and the provisional-handle mechanism.
- [`docs/BUILD.md`](docs/BUILD.md) — prerequisites, AutoCAD package versions per release and how to retarget, the `ExcludeAssets=runtime` rule, NETLOAD, and setting up a debug session against `acad.exe`.
- [`docs/TOOLS.md`](docs/TOOLS.md) — the complete agent tool surface, coordinate/units/handle conventions, the BYLAYER rule, and the uniform error envelope.
- [`docs/SAFETY.md`](docs/SAFETY.md) — the safety design for life-safety/AEC work.

---

## Disclaimer

mojoCAD is a **reference implementation**. AI output can be wrong, and CAD work for the built environment carries real-world safety consequences. **Verify everything** the agent proposes — dimensions, layers, code parameters, and the impact of every staged operation — before you accept it, and review the result after it is applied. mojoCAD is a tool that drafts under your supervision; it is not a substitute for professional judgement. **You are the engineer of record.**
