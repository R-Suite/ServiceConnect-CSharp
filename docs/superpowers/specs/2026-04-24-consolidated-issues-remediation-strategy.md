# Consolidated-issues remediation — phasing strategy

**Date:** 2026-04-24
**Source list:** [`consolodated-issues/2026-04-24-consolidated-issues.md`](../../../consolodated-issues/2026-04-24-consolidated-issues.md)
**Branch:** `v7-clean-architecture`
**Source-list HEAD anchor:** `cc3fd44a`
**Total items in scope:** 99 (8 Critical + 12 High + 25 Medium + 54 Low — counts include reclassified items kept for fix; see Appendix A for the relationship to the 96-confirmed headline number)

## 1. Summary

The consolidated issues list captures 96 confirmed defects (after Pass-2 verification) plus three reclassified items kept for remediation, distributed across streaming, RabbitMQ transport, core bus / processors, InMemory persistence, MongoDb persistence, telemetry, and interfaces. v7 has not shipped (latest tag `v3.0.0`), and the active branch is a deliberate breaking-change rewrite, so API-surface changes are on the table.

This document is the strategy. It defines:

- A Phase 0 mini-spec that locks five contract-level decisions before any interface-touching code is written.
- Phases 1–5 that fix all Critical and High items, grouped by subsystem to keep PRs focused and risk-falling fast.
- Phases 6a/6b that fix all Medium items.
- Phases 7a/7b that sweep all Low items.
- A per-phase workflow, tracker rules, and cross-phase concerns.

Per-phase implementation plans are written **one at a time** (`superpowers:writing-plans`) when each phase is reached, grounded against the then-current HEAD. This document is the authoritative reference each plan cites.

## 2. Phase 0 — contract design decisions

Phase 0 is a brainstorming-style mini-spec, not implementation. Output: a single design doc at `docs/superpowers/specs/2026-04-24-contract-decisions.md` that locks in answers to the five questions below. Phases 5, 6b, and parts of 7b consume those answers; until Phase 0 lands, those phases cannot be planned without churn.

### 2.1 Decisions to lock in

1. **H-22 — `IFilter` / `IFilterPipeline` return semantics.** Today the booleans are inverted (`IFilter.ProcessAsync` returns `true = continue`; `IFilterPipeline.ProcessAsync` returns `true = blocked`). Options to evaluate during Phase 0:
   - Convert both to an enum (`FilterAction.Continue` / `FilterAction.Stop`).
   - Standardise to one boolean convention with a method rename.
   - Leave the surface and improve documentation.

2. **C-08 / H-20 — `ITimeoutStore` id-only Remove/Release behaviour.** Today the InMemory store mutates state unconditionally on the id-only overloads; the MongoDb store silently no-ops on leased rows. Options:
   - Remove the id-only overloads and force callers to the lease-aware ones.
   - Add a result discriminator (`Removed` / `NotFound` / `Leased`) to the id-only overloads.
   - Align the InMemory store with MongoDb's silent no-op on leased rows.
   - Document the divergence as intentional (no fix beyond docs).

3. **M-28 / M-29 — `CancellationToken` on handler / write-stream interfaces.** `IMessageHandler.HandleAsync`, `IProcessHandler.HandleAsync`, `IMessageBusWriteStream.WriteAsync` / `CompleteAsync`. Options:
   - Breaking signature change (require `CancellationToken`).
   - Add an overload with a default implementation.
   - Document `IConsumeContext.CancellationToken` as canonical and skip the signature change.

4. **M-30 / M-31 — Options-type mutability.** `RequestOptions` is a mutable sealed class while `PublishOptions` / `SendOptions` are records; `SendOptions.EndPoints` is a mutable `IList<>` while headers are read-only. Options:
   - Convert `RequestOptions` to a record and `SendOptions.EndPoints` to a read-only collection (consistent across all options types).
   - Leave the surface mixed.

5. **M-32 — `HeaderDecoder.Decode` nested-table fallback.** Existing test asserts the broken type-name-string fallback for nested tables / arrays. Options:
   - Fix and update the test to assert recursive decoding.
   - Document the fallback as intentional and leave the broken behaviour.

### 2.2 Phase 0 output

A short design doc capturing:

- The decision per item (one of the options above).
- A one-paragraph rationale per decision.
- A list of consuming phases / items per decision so each downstream phase can find its inputs.

Phase 0 is complete when the doc is written, reviewed, and committed.

## 3. Phase issue assignments

### 3.1 Phase 1 — Critical+High: Streaming (5 items)

| ID | Title |
|---|---|
| C-01 | StreamProcessor final-packet double-dispatch |
| C-02 | StreamProcessor failed Write/SetLastPacketNumber wedges sequence |
| C-03 | MessageBusWriteStream packet-number-before-send gap |
| H-01 | Duplicate packet → poison loop (idempotent-ack) |
| H-02 | Eviction `TryRemove`-by-KVP races with `LastSeenUtc` |

### 3.2 Phase 2 — Critical+High: RabbitMQ transport (5 items)

| ID | Title |
|---|---|
| C-04 | Consumer Dispose→Start stale `_connection` |
| C-05 | Consumer `_clients` bag leak |
| H-05 | Producer Dispose disposes semaphores held by in-flight publishers |
| H-06 | Producer Dispose can publish/create state after teardown |
| H-07 | Producer Dispose 60s worst-case (shared budget) |

### 3.3 Phase 3 — Critical+High: Core / Processors (4 items)

| ID | Title |
|---|---|
| C-06 | ProcessManagerProcessor static `MapperCache` + root-provider scope bypass |
| H-10 | AggregatorProcessor batch-path / Dispose race |
| H-11 | AggregatorProcessor.ResetTimer creates duplicate Timers under contention |
| H-12 | StreamProcessor / AggregatorProcessor resolve from root provider (groups with C-06 — same DI architectural fix) |

### 3.4 Phase 4 — Critical+High: Persistence (4 items)

| ID | Title | Phase 0 dependency |
|---|---|---|
| C-08 | InMemoryTimeoutStore id-only Remove/Release ignores lease | Decision 2 |
| C-09 | MongoDb `GetTimeoutsBatchAsync` `nextPipeline` dead code | — |
| H-15 | CacheProvider.PurgeNormalPriorities key-only `TryRemove` | — |
| H-20 | MongoDb id-only Remove/Release contract clarity (paired with C-08) | Decision 2 |

### 3.5 Phase 5 — Critical+High: Telemetry + Interfaces (2 items)

| ID | Title | Phase 0 dependency |
|---|---|---|
| H-21 | `messaging.destination.name` set to routing key not exchange | — |
| H-22 | IFilter / IFilterPipeline inverted semantics | Decision 1 |

Critical+High subtotal across Phases 1–5: 8 Critical + 12 High = 20 items.

### 3.6 Phase 6 — Medium fixes (25 items, two sub-phases)

#### Phase 6a — Streaming + RabbitMQ + Core + Persistence (15 items)

- Streaming: M-01, M-02, M-03
- RabbitMQ: M-04, M-06, M-08
- Core: M-11, M-12, M-13
- Persistence: M-16, M-18, M-19, M-20, M-21, M-22

#### Phase 6b — Telemetry + Interfaces (10 items, consumes Phase 0)

- Telemetry: M-23, M-24, M-25, M-26
- Interfaces: M-27, M-28 (Decision 3), M-29 (Decision 3), M-30 (Decision 4), M-31 (Decision 4), M-32 (Decision 5)

### 3.7 Phase 7 — Low sweep (54 items, two sub-phases)

#### Phase 7a — Core + RabbitMQ + InMemory + MongoDb (28 items)

- Core: L-01, L-02, L-06, L-07, L-11, L-12, L-14
- RabbitMQ: L-15, L-18, L-20, L-21, L-22 + reclassified H-03, H-08
- InMemory: L-25, L-26, L-27, L-28, L-29
- MongoDb: L-33, L-35, L-37, L-38, L-39, L-40, L-41, L-42, L-43

#### Phase 7b — Telemetry + Interfaces (26 items)

- Telemetry: L-44, L-45, L-46, L-47, L-48, L-49, L-50, L-51, L-52, L-53, L-55, L-56, L-57
- Interfaces: L-58, L-59, L-62, L-63, L-64, L-67, L-68, L-69, L-70, L-71, L-72, L-73, L-75

## 4. Per-phase workflow

Phase 0 is a one-off (its deliverable is a design doc, not code). Phases 1–7 follow a fixed eight-step workflow:

1. **Re-verify against current HEAD.** Read each item's location, confirm the bug is still present, and note items already fixed out of band.
2. **Invoke `superpowers:writing-plans`** to produce `docs/superpowers/plans/2026-04-24-phase-<N>-<topic>.md`. The plan is grounded in the then-current HEAD.
3. **User reviews the plan** before any code.
4. **Implement via TDD** (red → green per item). Use `superpowers:subagent-driven-development` where the plan flags independent items for parallel execution.
5. **Verify** before claiming done:
   - `dotnet build` across the solution.
   - Full unit test suite.
   - Integration tests where applicable, wrapped in `sg docker -c '…'` for Testcontainers (RabbitMQ for Phase 2 / 6a / 7a; MongoDb for Phase 4 / 6a / 7a).
   - End-to-end runs of relevant `examples/` projects for phases that change observable behaviour or fix races only reachable from a real broker (see §5.3).
   - `website/` and `README` content sweep where the phase changed observable behaviour or public-API shape (see §5.4).
6. **Update the tracker** (see §5.1).
7. **Commit** the phase as one or more coherent chunks. Pattern: `fix(<subsystem>): …` per item or per logical group; a separate `docs(tracker): flip … done` commit at the end of the phase. No issue identifiers in code comments (see §5.5).
8. Move to the next phase.

## 5. Cross-phase concerns

### 5.1 Tracker

Status is flipped inline in `consolodated-issues/2026-04-24-consolidated-issues.md`. Every confirmed item gets a `**Status**: fixed in <short-sha>` line appended to its entry as the phase completes. The `Counts` table at the top of the file gains a `Fixed` column updated at the end of each phase. Rejected and PARTIAL-deferred items remain as-is. No separate tracker file.

The strategy spec itself gains a one-line `Phase status` entry per phase as phases close, so the spec stays a live index of progress without rewriting its content.

### 5.2 Contract-parity matrix

The matrix at the bottom of the consolidated-issues doc is touched by Phase 4 (TimeoutStore id-only contract) and Phase 6 (M-19 batch cap, M-21 Version bump). Each phase that resolves a divergence updates the matrix row from "Divergent" to "Aligned" alongside its tracker flip.

### 5.3 Test coverage tiers

Three tiers, applied as the plan dictates:

- **Unit (always):** TDD red→green per fix.
- **Integration (where applicable):** Testcontainers, wrapped in `sg docker -c '…'`.
- **End-to-end (where applicable):** the `examples/` projects run end-to-end against live brokers and exercise real saga / aggregator / process-manager flows. Phases that change observable behaviour or fix race conditions reachable only from a real broker get an e2e smoke run as part of phase verification, and a new e2e test added if the bug is reproducible end-to-end. Up-front candidates: C-04, C-05, H-05, H-06, H-07, C-06, H-10, H-11, H-21.

New tests live in the existing test project for the touched subsystem; integration / e2e tests live in the integration project for that subsystem.

### 5.4 Website and README

`website/` content is part of the phase. Any phase that changes observable behaviour or public-API shape updates the website content for that subsystem in the same phase, not deferred. Affected phases up-front:

- Phase 4 may touch persistence-contract docs depending on Phase 0's TimeoutStore decision.
- Phase 5 — telemetry attribute names; IFilter semantics.
- Phase 6b — `CancellationToken` signatures, options-type shapes, header decoding.
- Phase 7b — interface evolution items.

`README` is updated alongside `website/` in the same phase. The pattern matches the existing `docs(website,readme): sync docs with v7 code after audit` commit.

### 5.5 Comment style — code-level rule

No issue identifiers in code comments. No `// fix C-08`, no `// addresses H-22`, no "previously did X" / "before the fix" references. Comments describe what the implementation does and the why behind a non-obvious choice. Issue identifiers belong in the commit message body and the eventual PR description, never in source.

### 5.6 New bugs discovered mid-phase

If implementation reveals a new bug not in the consolidated list:

- If trivial and inside the same file as the fix in flight, fix it inline and call it out in the commit body.
- If non-trivial or out of scope, add a new entry to the consolidated-issues doc under the appropriate severity, cite "discovered during phase N", and defer.

### 5.7 Branching

Stay on `v7-clean-architecture` end-to-end. No per-phase branches. The eventual v7 PR (draft already in `PR-MESSAGE.md`) rolls everything up. After Phase 7 closes, `PR-MESSAGE.md` gains a "Bug-fix delta" section enumerating fixed items by ID.

### 5.8 Phase ordering

Strictly sequential: Phase 0 → 1 → 2 → 3 → 4 → 5 → 6a → 6b → 7a → 7b. Phase 0 is a hard gate for Phases 5, 6b, and the interface portion of 7b. Within a phase, the implementation plan flags items that can be worked in parallel via `superpowers:subagent-driven-development`.

## 6. Spec and plan output paths

| Artefact | Path |
|---|---|
| This strategy spec | `docs/superpowers/specs/2026-04-24-consolidated-issues-remediation-strategy.md` |
| Phase 0 contract-decisions spec | `docs/superpowers/specs/2026-04-24-contract-decisions.md` |
| Phase 1 plan | `docs/superpowers/plans/2026-04-24-phase-1-streaming-critical-high.md` |
| Phase 2 plan | `docs/superpowers/plans/2026-04-24-phase-2-rabbitmq-critical-high.md` |
| Phase 3 plan | `docs/superpowers/plans/2026-04-24-phase-3-processors-critical-high.md` |
| Phase 4 plan | `docs/superpowers/plans/2026-04-24-phase-4-persistence-critical-high.md` |
| Phase 5 plan | `docs/superpowers/plans/2026-04-24-phase-5-telemetry-interfaces-critical-high.md` |
| Phase 6a plan | `docs/superpowers/plans/2026-04-24-phase-6a-medium-internal.md` |
| Phase 6b plan | `docs/superpowers/plans/2026-04-24-phase-6b-medium-telemetry-interfaces.md` |
| Phase 7a plan | `docs/superpowers/plans/2026-04-24-phase-7a-low-internal-sweep.md` |
| Phase 7b plan | `docs/superpowers/plans/2026-04-24-phase-7b-low-telemetry-interfaces-sweep.md` |

## 7. Phase status

(Updated as each phase closes.)

- Phase 0: complete (design doc committed in 45b3811e)
- Phase 1: complete (5 items, commits 2e8c9d76 0569dc65 5400117c 64217763 c9d24bb5)
- Phase 2: complete (5 items, commits 7e3a261b 21eae3b2 107ab1c7 049c00a8 a4791a97)
- Phase 3: complete (4 items, commits c39be6aa f3e78855 8ef58c98 e4aaf12c 5de3bdf6 e4e401ec 5fbd4458 12578252 b581c4b0)
- Phase 4: complete (4 items + L-73 side-effect, commits ca5213be 7326d9fb c03acc72 a315d2d7 1bff138b d8a707fe)
- Phase 5: complete (2 items, commits 36b20c27 15b44fe8 23c3d25b 4e0f448f 0e45702d dac580e6 06bf8bd1 73d8fcf1 2b37e279)
- Phase 6a: complete (11 items, retroactive 4, commits ffbdbbd6 33c157ea e97e244a bfaccb84 f43e03f3 1d428816 1f04e14c f9bed011 8b071649 3858f056 9a712f35 195fe9bb ccd5f56a a830c6d5 d4823c68 54949ab1 1a40bc47 2af05de1 f163fdf4)
- Phase 6b: complete (10 items + L-58 side-effect, commits c11aa54e 248e4502 44117b12 cbd970bd 40ccb64b dff4c22e f23fc7bc 0110a272 547335f9 b7f43616 4457d85f 29e92c3a)
- Phase 7a: not started
- Phase 7b: not started

## Appendix A — Item-count reconciliation

The consolidated-issues `Counts` table reports 96 *confirmed* items after Pass-2 verification (8 Critical + 11 High + 25 Medium + 52 Low). This strategy plans for 99 items because it includes three items that the headline number excludes but that the source-list entries still mark as kept-for-fix:

- **H-20** — Pass-2 verdict is PARTIAL: the silent-no-op behaviour is documented for canonical lease-aware callers, but the third-party UX gap is kept under High as a contract-clarity issue. Planned in Phase 4 (paired with C-08).
- **H-03** — reclassified to cosmetic / Low hygiene; planned in Phase 7a.
- **H-08** — reclassified to latent / Low hygiene; planned in Phase 7a.

These three items appear in the source-list severity sections under their original IDs and are counted in the headline as rejected/reclassified; this strategy adopts them at the appropriate target severity and counts them once.

C-09 stays in the 8 Critical confirmed (the source list keeps it under Critical until the dead code is removed) and does not contribute to the +3 reconciliation.

## Appendix B — Items intentionally out of scope

The consolidated-issues doc records all rejected and PARTIAL-deferred items in its trailing "Rejected" section. Those are explicitly out of scope for this remediation. Future reviewers consulting this strategy doc should refer back to the source list's "Rejected" section for the full list and the rationale per item; that section is authoritative and is not duplicated here.
