# Architecture Review Follow-Up — Phasing Overview

**Source review:** [2026-05-17-architecture-review.md](./2026-05-17-architecture-review.md)
**Date:** 2026-05-17
**Branch base:** `v7-clean-architecture`

This document is the high-level roadmap for acting on the 2026-05-17 review. Each phase is **plan-independent**: its detailed plan can be written without referencing other phases' plans. Phases ship sequentially by default, but Phase 3 sub-plans (3a/3b/3c) are parallelizable.

---

## Workflow rules (apply to every phase)

These rules are non-negotiable for the duration of this refactor stream. They live here so every phase plan can reference them rather than redefining them.

1. **Validate before implementing.** Before touching any finding from the source review, re-read the cited code first-hand and confirm the finding is not a false positive. The source review's audit pass produced four "Critical" claims that were wrong on second read; treat every remaining finding as a hypothesis until first-hand verification. If a finding is a false positive, record it in `docs/reviews/2026-05-17-rejected-findings.md` (create if absent) and skip it — do not silently drop.
2. **Per-change code review.** After each finding's change lands (file edits + tests), run `superpowers:requesting-code-review` on that change before moving to the next finding.
3. **End-of-phase verification gate.** Every phase ends with this block — none of these steps is optional, and none rolls forward to a later phase:
   - Full code review of all phase changes.
   - Full unit-test suite green (delegated to subagent per project workflow rules).
   - Full end-to-end suite green (delegated to subagent; requires docker for Mongo + RabbitMQ).
   - All example apps build and smoke-run successfully against a local broker.
   - Documentation website (`docs/`) and `README.md` reflect any public-API or behavior changes from the phase.
4. **Docs in the same PR.** When a change alters public API, configuration shape, or visible behavior, the documentation update is part of *that* PR — never deferred.

---

## Phase 1 — Localized correctness & observability fixes

**Goal:** Ship eight self-contained fixes as small independent PRs. No new collaborators, no public API change.

**Findings in scope:**
- `Producer.cs:206/:227` — fixed-delay retry → capped exponential backoff + jitter; apply same to `RabbitMqConsumerHost` recovery
- `InboundMessageProcessor.cs:168-193` — audit-publish silent drop → emit `messaging.message.dropped` counter, promote log to `LogError`
- `RabbitMqConsumerHost.cs:672-673` — fire-and-forget deadline-helper task → surface faults at `LogWarning`
- `AggregatorProcessor.cs:152-172` — `ResetTimer` previous-timer dispose moved inside `_resetTimerLock`
- `ConsumeContext.cs:148-177` — `IsKnownQueue` O(n²) nested loop → flattened `HashSet<string>`
- `Bus.cs:1032-1062` — `BuildRoutingSlip` duplicate destination re-validation removed
- `Bus.cs:541-561` — `ReadInboundHopsCompleted` → extract `TryParseInt32Header` utility
- `Bus.cs:952-956` — `ReservedHeaders` StringComparer.Ordinal rationale comment

**Detailed plan:** to be written next (this conversation).

---

## Phase 2 — Outbound pipeline extraction

**Goal:** Eliminate the ~200 LOC of duplication across `Bus.Publish/Send/SendToMany/RouteAsync` and `RequestReplyManager.SendRequest*` paths. One coherent refactor, one PR.

**Findings in scope:**
- `Bus.cs:123-176, 179-216, 219-325, 446-538` — duplicate serialize → outgoing-filter → header-stamp preamble
- `RequestReplyManager.cs:32-175` — `SendRequestAsync` / `SendRequestMultiAsync` duplication
- `RequestReplyManager.cs:115-175, 322-325` — repeated `ContinueWith` UnobservedTaskException-suppression block

**Deliverables:**
- New `OutboundMessagePipeline` internal collaborator
- `Bus.cs` shrinks from 1106 → ~850 LOC
- `RequestReplyManager` shares one core request-send method
- New `SuppressUnobservedFault(Task)` static helper

**No public API change.**

---

## Phase 3 — Monolith decomposition (SRP splits, parallelizable)

**Goal:** Bring the four largest production files under their cohesion budget. Three independent sub-plans; can be parallelized across sessions.

**3a — `ServiceConnectActivitySource` (795 LOC) split**
- Target: 3 files (`ServiceConnectActivitySource` + `TraceContextPropagation` + `TelemetryEnrichment`)
- No public API change (internal collaborators)

**3b — `RabbitMqConsumerHost` (962 LOC) split**
- Extract `RabbitMqChannelHost` (channel lifecycle ownership) and broker-event routing
- Host becomes a coordinator; admission gate and dispatch pipeline already extracted

**3c — `MessageDispatcher.DispatchAsync` (193-line method) split**
- Extract `TryResolveMessageType`, `RunDispatchPipelineAsync`, `HandleDispatchErrorAsync`
- Rename single-letter params (`mb` → `messageBytes`, etc.) in same change

---

## Phase 4 — Public API / configuration / contract changes

**Goal:** Coherent design pass on public-surface findings. Likely a minor or major version bump.

**Findings in scope:**
- Outgoing filter `Stop` asymmetry — pick `SendResult` return vs uniform `OutgoingFiltersBlockedException`
- `IProducer` default-shim diagnostics — `SupportsRoutingKey` capability flag + shim-fallback log
- Typed `RabbitMqOptions` via `IOptions<>` — replace string-keyed `IDictionary`, retain dictionary as back-compat shim
- `Consumer` lifecycle vs dispose semaphore — separate budgets so startup-wedge can't block SIGTERM
- Promote `ConsumeContextAccessor` / `ConsumeScopeAccessor` behind interfaces
- `MessageRetryHandler` — drop `logAsMaxRetries` flag, split retry-publish from terminal-publish

**Plan should be written only after Phase 1–3 land, so the public-API change surface is the only thing in flight.**

---

## Phase independence matrix

| Phase | Touches public API? | New collaborators | Files significantly modified | Depends on prior phase? |
|---|---|---|---|---|
| 1 | No | None | `Producer`, `RabbitMqConsumerHost`, `InboundMessageProcessor`, `AggregatorProcessor`, `Bus`, `ConsumeContext` (small, targeted edits) | None |
| 2 | No | `OutboundMessagePipeline` | `Bus`, `RequestReplyManager` | None (independent of P1 because P1 edits don't touch the duplicated preamble blocks) |
| 3a | No | Split-files | `ServiceConnectActivitySource` and new siblings | None |
| 3b | No | `RabbitMqChannelHost` | `RabbitMqConsumerHost` | None (P1 fire-and-forget fix in same file is small + localized; rebase trivial) |
| 3c | No | Split-methods | `MessageDispatcher` | None |
| 4 | **Yes** | Typed options, capability flags | `IBus`, `IProducer`, `Consumer`, options classes | None — but ship last for cleanest diff |

---

## What's explicitly **not** in scope

- The four findings rejected on re-read in the source review (StreamProcessor CAS, AggregatorProcessor OCE, EvictStaleStreams iteration, IBus:288 LSP). Reasoning is documented in the source review's "Findings rejected" section.
- Low-severity InMemory perf and DeepClone JSON round-trip — flagged as "dev/test only is acceptable" in the review.
- Mongo `SemaphoreSlim` non-disposal — pattern is intentional; a doc improvement may piggyback in 3a but does not warrant its own plan.
