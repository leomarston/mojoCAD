# Safety design

mojoCAD is built for architectural, MEP and fire/life-safety work, where an incorrect or silently-applied edit can have real-world consequences. Its safety design is not a setting you can turn off — it is the structure of the system. This document describes the guarantees and the mechanisms that enforce them.

## The core invariant: no auto-apply

> **The agent's only path to the drawing is `emit_changeset` → human review → atomic apply.**

- Write tools do **not** touch the AutoCAD `Database`. They stage an AutoCAD-free `ProposedOp` into the turn's change set (`ChangeSetBuilder`). The agent and the OpenRouter loop have no reference to AutoCAD types at all — the architecture makes a direct write impossible, not merely disallowed.
- To affect the drawing the agent must call the `emit_changeset` control tool, which finalises the staged ops into a `ChangeSet` and hands it to the engineer for review.
- **There is no auto-apply mode, by design.** Even a fully-confident agent stops at a reviewable change set. The system prompt states this explicitly: *"You NEVER modify the drawing directly… There is no auto-apply mode, by design."*
- The accepted subset commits as one document-locked transaction under a single named undo group. It is **all-or-nothing**: if any operation throws, the whole apply rolls back and nothing is committed, so you never get a half-applied life-safety change. An inline **Undo** affordance reverses the last applied change set (`_.U`).
- **Trustworthy counts.** The review card displays `ChangeSet.ComputeStats()` — counts derived from the operations themselves, never any number supplied by the model — so the AI cannot misrepresent what it is about to do.
- **Preview without commitment.** Staged changes are rendered as transient graphics (green = new, amber = modified ghost, red = doomed) with **no database writes**. Rejecting is therefore free: the drawing is never dirtied, no reactors fire, autosave is untouched.

## Plan-before-act and the approval gate

The agent's mandatory per-turn workflow is: ground in the live drawing → **present a plan** → stage one coherent unit of work → self-check key dimensions → present the change set.

- `present_plan` emits a short numbered plan *before* any geometry is staged, so the engineer can read the intent and stop the agent early.
- For **Fire & Life-Safety** projects — or any project where you enable **Require plan approval** — the plan becomes an **approval gate**: after presenting it the agent **stops** and waits for the engineer to approve before staging anything. The gate is forced on whenever the discipline preset is `FireAndLifeSafety`, regardless of the per-turn setting.

## `ask_clarification` for code parameters

The agent is instructed, as a non-negotiable rule, to **never guess a life-safety parameter** — fire-resistance rating, egress/corridor width, occupant load, sprinkler spacing/coverage, or travel distance. If such a value is not supplied, it must call `ask_clarification`, which ends the turn until the engineer answers.

This is reinforced at multiple layers:

- The **system prompt** lists the rule explicitly and adds *"A correct clarifying question is always better than a confident wrong edit."*
- The **tool descriptions** mark `ask_clarification` as MANDATORY for unspecified life-safety parameters, and warn against guessing (e.g. `place_sprinklers`: *"NEVER guess spacing for life-safety — confirm it with the engineer first."*).
- The **domain tools** never invent these values; `place_sprinklers` reports the *achieved* spacing back to the model and to the engineer with a reminder to verify against the applicable code (e.g. NFPA 13).

## The lint gate

Before the engineer accepts, `StandardsLint` inspects the change set against the active `StandardsProfile` and attaches `LintFinding`s to the review card. It is deliberately conservative — it nudges toward standards, it never silently "fixes":

- **BYLAYER discipline** — a per-entity colour/linetype/lineweight override (instead of relying on the layer) raises a `Notice` with a hint to use a layer that carries those properties.
- **Layer-naming standard** — additive geometry on a layer that doesn't look like an AIA/NCS name (when that standard is active) raises an `Info` hint to confirm it matches the project CAD standard.
- **Geometry sanity** — obvious footguns raise a **`Blocking`** finding: non-positive circle/arc radius, a polyline with fewer than two vertices, a non-positive scale factor, non-positive text height.

A **`Blocking`** finding (or a `Blocking`-severity op) **cannot be accepted** until the underlying issue is resolved — `AcceptAll` skips blocking ops, and the review card prevents committing them.

## Deletions

Deletions get special treatment so they're never swept up accidentally:

- Erase operations are categorised `Erase` and previewed in **red** on the doomed entities.
- They are staged at **`Warning`** severity by default so they stand out.
- `ChangeSet.AcceptAll(includeErase = false)` **excludes erases** — "accept all" never deletes; the engineer must opt into each deletion.
- The system prompt instructs the agent to prefer the smallest change and **not erase or move existing geometry unless explicitly asked**.

## The audit log

Every applied change set is recorded by `JsonlAuditLog` to an append-only, one-file-per-drawing trail under `%APPDATA%\mojoCAD\audit`. Each `AuditEntry` records the accountability facts for life-safety work:

- drawing key, change-set id, UTC timestamp;
- the **model** that produced it;
- the plain-language **summary** and per-op descriptions;
- the applied op count and the undo label.

The audit log deliberately records **only what was applied** — never the API key, and never the model's raw chain-of-thought.

## Key storage and data protection

- **DPAPI-encrypted key.** The OpenRouter API key is stored by `DpapiSecureStore` using Windows `ProtectedData` (CurrentUser scope) plus an application-specific entropy value, written to `%APPDATA%\mojoCAD\apikey.bin`. It is decryptable only by the same Windows user on the same machine, and **never** appears in clear text on disk, in logs, in the drawing, or in the repository. Writes are atomic (temp + move); a blob that won't decrypt (e.g. copied to another machine) is treated as "no key". The key is **not** part of `config.json`.
- **Provider `data_collection: deny`.** By default (`ModelConfig.DenyDataCollection = true`) mojoCAD sends OpenRouter the provider preference `data_collection: "deny"`, so prompts containing your proprietary CAD data are not routed to providers that log or train on them. The request also sets `require_parameters: true` so only providers that honour the tool schema are used.
- **Transparent prompting.** The system prompt is templated from the user-visible `StandardsProfile` (discipline, layer standard, code references, annotation scale, house rules) and a fresh snapshot of the drawing. The engineer can see and override exactly how the AI is being instructed — nothing about its behaviour is hidden.

## Your responsibility — verify everything

mojoCAD is a drafting copilot that works **under your supervision**. It reduces tedium and surfaces precise, reviewable proposals; it does not replace engineering judgement, code knowledge, or your duty of care.

- **Verify everything before you accept** — dimensions, layers, code parameters, and the impact of each operation. The preview and the computed stats are there to make this fast; use them.
- **Re-check after apply** — confirm the result on the canvas and in the model.
- **Never accept a life-safety parameter you didn't supply or independently confirm.** If the agent asks, answer with a value you can stand behind.

**You are the engineer of record.** mojoCAD's safety mechanisms exist to give you control — they do not transfer responsibility away from you.
