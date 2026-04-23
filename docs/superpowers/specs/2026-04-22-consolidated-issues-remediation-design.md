---
name: Consolidated issues remediation (round 2) — v7 branch
description: Phased plan for fixing the 48 issues in consolodated-issues/2026-04-22-consolidated-issues.md on the v7-clean-architecture branch
type: design
date: 2026-04-22
branch: v7-clean-architecture
---

# Consolidated Issues Remediation (Round 2) — Design

## Goal

Fix all verified issues from [consolodated-issues/2026-04-22-consolidated-issues.md](../../../consolodated-issues/2026-04-22-consolidated-issues.md) on the `v7-clean-architecture` branch.

Totals: **1 Critical · 8 High · 22 Medium · 14 Low · 3 Uncertain = 48 items.**

This is the second consolidated-issues exercise on this branch. The 2026-04-21 round (62 items, mostly landed) focused on trust-boundary, DI/pipeline, and transport fundamentals. This round covers residual lifecycle/leak/observability issues surfaced by a follow-up review pass.

## Constraints (from brainstorming)

- **Partition:** Hybrid, severity-major and subsystem-minor. Within a phase, groups are ordered Core → Interfaces → RabbitMQ → MongoDB → InMemory → Telemetry.
- **Uncertain handling:** Split. The two potentially-Critical/High Uncertain items get investigated up front in Phase 0; the Low one is tackled in Phase 4.
- **PR strategy:** One PR at the end (after Phase 4). No incremental merges. Phases build freely on each other.
- **Plan docs:** This design now; each phase's implementation plan is written **just before** that phase begins (so it reflects the code state left by prior phases).
- **Per-group workflow:** Fully autonomous. No per-group or per-phase approval gate. Stop-and-ask only for defined exception conditions.
- **Review:** End-of-branch only. User reviews the full branch diff before the PR goes up.
- **Tracking:** Checkboxes in the consolidated issues doc, updated in the same commit as each fix.
- **No new features, no unrelated refactoring.**

## Phasing

### Phase 0 — Targeted investigation (2 groups, no fixes)

Write tests for the two Uncertain items with Critical/High blast radius:

- **Mongo `AggregateFacetResult<T>` pattern-match** — integration test against real MongoDB via Testcontainers (`sg docker -c`). Insert `TimeoutData` with `Time <= utcNow`, call `GetTimeoutsBatchAsync`, assert the result contains the inserted row. Disconfirmation is a first-class outcome: commit the test anyway to protect against regression.
- **Poison-message infinite redelivery** — RabbitMQ Testcontainer. Start consumer with an always-throwing handler, sabotage the retry-publish path (e.g. delete the retry exchange), assert redelivery count is bounded within a fixed window.

Each investigation commits one of:
- **Confirmed** — test fails without a fix; item is promoted to Phase 1 as a Critical/High group. **Stop-and-ask** to confirm re-sequencing.
- **Disconfirmed** — test passes; commit the test, mark the issue `[-] disconfirmed by <test path>` in the consolidated doc, continue.

Third Uncertain item (AggregatorProcessor timer-fired flush, narrow-window Low) is deferred to Phase 4.

### Phase 1 — Critical + High (≈7 groups)

9 items minimum (1 Critical + 8 High); more if Phase 0 promotes any Uncertain.

Subsystem order within phase:
- **Core:** `ConsumeContextPool` cross-message leak (Critical); Producer publish timeout; `MessageBusReadStream` non-contiguous packets; `IRequestReplyManager` split-brain registration.
- **Interfaces (breaking):** `Aggregator<T>.Execute` → `ExecuteAsync`; `IStreamHandler<T>.Execute` → `ExecuteAsync`. Paired in one commit.
- **MongoDB:** lease-aware Remove/Release result-count inspection; `GuidRepresentationMode` V2→V3 robustness.
- **InMemory:** parity — implement `ILeaseAwareTimeoutStore`. Paired in the same commit as the Mongo lease-aware fix, since both are the same class of bug.

### Phase 2 — Medium (≈11 groups)

22 items across Core, RabbitMQ, MongoDB, InMemory, Telemetry, Interfaces.

Expected cluster shape:
- **Core lifecycle trio** — `Bus.StopConsumingCoreAsync` unconditionally sets `_stopped`; `SemaphoreSlim` disposed-while-waiting race; missing OCE handling in `StopConsumingCoreAsync`. All `Bus.cs` DisposeAsync/Stop path, same reasoning — one commit.
- **Bus header spoofing** — `MessageType` + `CorrelationId` become Bus-authoritative, matching the 2026-04-21 MessageId hardening.
- **DI/scanning** — `ScanAssemblies` ignored under `ScanForMessageHandlers=false`; handler singleton guard misses factory registrations.
- **Dispatcher** — `MessageDispatcher` returns non-failure for untracked/stale replies.
- **RabbitMQ consumer lifecycle** — `Consumer.DisposeAsync` only swallows ODE; `Consumer.StartConsumingAsync` non-idempotent; no broker-initiated cancel/connection event subscription.
- **RabbitMQ message processing** — Producer dispose leak on lock timeout; audit-publish silent drop on non-empty routing key; `RetryCount` header decode for interop; null-valued `TypeName` header crashes dispatch.
- **MongoDB PM startup** — index marker flipped before `CreateOneAsync` awaits; bare catch on concurrent index creation (no 85/86 discrimination).
- **MongoDB w:0 asymmetry** — `UpdateDataAsync` swallows; `DeleteDataAsync` spuriously throws.
- **InMemory** — `Provider.Keys()` race with external `IKeyValueStore` callers.
- **Telemetry triplet** — `Send` skips trace-context when Message is null; trace context not injected when telemetry flags are off; Activity status never set on failure.
- **Interfaces (breaking, isolated)** — `IMessageTypeRegistry.TryResolve` → `[MaybeNullWhen(false)] out Type? type`.

### Phase 3 — Low (≈6 groups)

14 items. Same subsystem order. Low-risk hardening: CAS races, silent exception swallows, cancellation-token plumbing on index creation, covariance bugs, polymorphic deep-clone, additional span tag.

### Phase 4 — Final Uncertain (1 group)

`AggregatorProcessor` timer-fired flush accessing disposed `_disposeCts`. Unit test with `FakeTimeProvider`, synchronised via `TaskCompletionSource` to hit the narrow window. If confirmed, fix and commit in the same group; if not, commit the test with `[-] disconfirmed` on the issue.

## Group sizing principles

One commit per group. Rules that determine group boundaries:

- **Critical items get their own group.** C1 (`ConsumeContextPool`) touches a hot path and needs its own targeted test.
- **Parity items pair up.** Same class of bug on two persistors (Mongo H6 + InMemory H8 lease safety) → one commit covering both.
- **Subsystem lifecycle items cluster.** Bus disposal/stop trio in one commit; RabbitMQ consumer lifecycle trio in one commit.
- **Breaking changes batched where related.** `Aggregator<T>` + `IStreamHandler<T>` async-Execute changes in one commit (same rework). `IMessageTypeRegistry.TryResolve` nullable annotation in its own Phase 2 commit (unrelated).
- **Never mix subsystems in one commit.** Group boundary is "same class of fix OR same file cluster," not "same phase."

Rough counts: Phase 0 = 2; Phase 1 ≈ 7; Phase 2 ≈ 11; Phase 3 ≈ 6; Phase 4 = 1. Total ≈ **27 commits** across 48 issues.

Exact groupings are defined in each phase's implementation plan, not here.

## Workflow

Fully autonomous per-group loop:

1. Plan the group (internally via TodoWrite / per-phase plan doc).
2. Implement + test + commit. Update consolidated-doc checkboxes in the same commit.
3. Move to the next group.

No per-group approval. No per-phase approval. End-of-phase posts a one-paragraph summary and immediately begins the next phase.

**Stop-and-ask conditions (only these):**

- **Phase 0 promotion** — if a Phase 0 test confirms an Uncertain item, stop to confirm re-sequencing before Phase 1 proceeds.
- **Scope change** — a planned fix requires touching files outside the group's scope, or forces an API/interface change not already scoped as breaking.
- **New bug surfaced** — material issue not in the consolidated doc. Log, flag, default to defer.
- **Approach mismatch** — writing the test reveals the planned fix is wrong for a Critical/High item and a different design is needed.
- **Test infrastructure gap** — a Critical/High fix can't be cleanly tested with existing infra.

## Breaking changes

Three items change public surface:

1. `Aggregator<T>.Execute(IList<T>)` → `ExecuteAsync(IList<T>)` returning `Task`. (Phase 1 group, paired with #2.)
2. `IStreamHandler<T>.Execute(...)` → `ExecuteAsync(...)` returning `Task`. (Same Phase 1 group.)
3. `IMessageTypeRegistry.TryResolve` gains `[MaybeNullWhen(false)] out Type? type`. (Phase 2 group, isolated.)

All three are appropriate on v7. I'll maintain a running list in a scratch file (`docs/v7-breaking-changes.md` or similar — path picked when pre-work commits land) for the eventual PR description.

## Test strategy

| Severity | Approach |
|---|---|
| Critical, High | New test per fix that would have caught the bug. |
| Medium, Low | Rely on existing suite. Add a test only if a clear gap exists. |
| Phase 0 Uncertain | The named test is the deliverable, regardless of outcome. |

**Commands:**
- Unit: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`
- Integration (Mongo + RabbitMQ Testcontainers): `sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj'`

**Test mutation:** allowed only when an existing test asserts buggy behaviour. Flagged in the group's commit message.

## Progress tracking

Each issue heading in the consolidated doc gets a `[ ]` prefix during pre-work. Progress summary block is added at the top of the doc:

```
Progress: Critical 0/1 · High 0/8 · Medium 0/22 · Low 0/14 · Uncertain 0/3 (updated 2026-04-22)
```

In each fix commit: `[ ]` → `[x] (commit: <sha>)`. Deferred items: `[-] <one-line reason>`. In-progress state is skipped (autonomous workflow; no visible "in progress" interval).

## Pre-work (single commit before Phase 0)

1. Baseline green — run both test suites on HEAD; fix any red before starting.
2. Docker smoke — `sg docker -c 'docker ps'`; confirm Testcontainers can pull RabbitMQ + MongoDB images.
3. Add `[ ]` prefix to every issue heading + progress summary block in the consolidated doc.
4. Branch confirmation — on `v7-clean-architecture`.
5. Commit: `chore(tracking): add progress checkboxes to 2026-04-22 consolidated issues doc`. No code changes.

## Commit message convention

```
<type>(<area>): <short summary referencing issue category>

<body: one sentence per issue covered, stating what changed and why.>

Refs: <consolidated-doc issue titles or line refs>
```

- `<type>`: `fix`, `refactor`, or `chore`.
- `<area>`: `rabbitmq`, `bus`, `dispatcher`, `persistence-inmem`, `persistence-mongo`, `telemetry`, `di`, `interfaces`, `aggregator`, `timeout`, etc.
- The consolidated doc doesn't use IDs like C1/H3 this round, so `Refs:` cites the issue title or file+line from the doc.

## Exit condition

- All issue checkboxes ticked or `[-]` deferred with rationale.
- Both test suites green.
- Progress summary reads `Critical 1/1 · High 8/8 · Medium 22/22 · Low 14/14 · Uncertain 3/3` (subject to defer adjustments).
- Running breaking-changes list (for PR description) complete.

## Out of scope

- PR creation / merge — user triggers.
- Release notes / changelog — only the breaking-changes scratch list is produced here.
- New features or refactoring beyond what each fix requires.
- Issues surfaced mid-flight that aren't in the consolidated doc — logged, deferred by default.
