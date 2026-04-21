# Consolidated Issues Remediation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix all 62 verified issues in [consolodated-issues/2026-04-21-consolidated-issues.md](../../../consolodated-issues/2026-04-21-consolidated-issues.md) on the `v7-clean-architecture` branch, in ~32 logical commits with per-group plan/approve/implement/test/report/approve/commit checkpoints.

**Architecture:** Each group (G#) is one commit covering one or more related issues. Each group follows an identical workflow — the per-issue code changes are worked out inside the group's "plan" step at execution time by reading the cited source lines and presenting a short mini-plan for user approval. This plan is an orchestration plan: it prescribes the workflow and contract for each group, not the line-level fixes (which emerge at runtime from the cited file:line references in the consolidated doc).

**Tech Stack:** .NET 10 / C#, xUnit, Testcontainers (RabbitMQ + MongoDB), Moq.

---

## Ground rules — every group follows this workflow

For **every** group G1–G32, the steps are:

1. **Read the cited source** at the file:line references from the consolidated doc for the issues in the group.
2. **Present a group mini-plan** to the user: files to change, approach per issue, risks, tests to add (Critical/High only), commit message. Wait for approval.
3. **Write failing tests** (Critical/High only, per design spec). Run them; confirm they fail on current code.
4. **Implement** the fix(es).
5. **Run tests** — unit always; integration if the group touches transport or Mongo persistence.
6. **Update the consolidated doc:** set `[x] (commit: <sha-pending>)` on each issue covered; refresh the progress summary line.
7. **Report** — summary of changes, test counts, diff overview.
8. **Wait for user approval** of the diff.
9. **Commit** — single commit including code, tests, and doc update. Format per spec:
   ```
   <type>(<area>): <summary>

   <body: one sentence per issue>

   Refs: <issue IDs>
   ```

**Do not merge groups.** **Do not skip the mini-plan gate.** **Do not commit without user approval of the diff.**

---

## Task 0: Pre-work

**Files:**
- Modify: `consolodated-issues/2026-04-21-consolidated-issues.md`
- Create: (none)

### 0.1 Confirm branch

- [ ] **Step:** Verify current branch

Run: `git branch --show-current`
Expected: `v7-clean-architecture`

If different, stop and ask the user.

### 0.2 Baseline unit tests

- [ ] **Step:** Run unit test suite on current HEAD, capture result

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --nologo`
Expected: all tests pass. Record pass/skip/fail counts in a scratch note.

If any test fails or errors out, **stop** and report to the user before proceeding.

### 0.3 Baseline integration tests

- [ ] **Step:** Confirm docker access works

Run: `sg docker -c 'docker ps'`
Expected: a table output (even if empty). No "permission denied" error.

- [ ] **Step:** Run integration test suite on current HEAD

Run: `sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --nologo'`
Expected: all tests pass. Record pass/skip/fail counts.

If any test fails, **stop** and report before proceeding.

### 0.4 Add progress checkboxes to consolidated doc

- [ ] **Step:** In `consolodated-issues/2026-04-21-consolidated-issues.md`, add a progress summary block directly under the frontmatter and before the first `##` heading:

```markdown
**Progress:** Critical 0/12 · High 0/20 · Medium 0/23 · Low 0/7  (updated 2026-04-21)
```

- [ ] **Step:** Add a checkbox prefix `[ ]` to every issue heading (every `### C#.`, `### H#.`, `### M#.`, `### L#.`). Preserve the original heading text.

Example transformation:
```
### C1. Reserved transport headers are caller-overridable
```
becomes:
```
### [ ] C1. Reserved transport headers are caller-overridable
```

- [ ] **Step:** Add an H15 footnote at the bottom of the doc, directly above the "Recommended fix order" section:

```markdown
> **Note:** H15 is intentionally absent from the list — the original review had a numbering gap.
```

- [ ] **Step:** Commit the checkbox-only change

```bash
git add consolodated-issues/2026-04-21-consolidated-issues.md
git commit -m "$(cat <<'EOF'
chore(tracking): add progress checkboxes to consolidated issues doc

No code changes. Adds [ ] prefix to every issue heading, a progress
summary block at the top, and an H15 footnote. The doc becomes living
state for the remediation effort.
EOF
)"
```

- [ ] **Step:** Verify commit landed

Run: `git log -1 --oneline`
Expected: shows the `chore(tracking)` commit.

---

## Task 1 — G1: Trust boundary (C1 + C5 + C9 + H7)

**Issues:** C1, C5, C9, H7 (composite)

**Files to read first:**
- `src/ServiceConnect.Client.RabbitMQ/Producer.cs` lines 466-483 (C1)
- `src/ServiceConnect/Services/MessageDispatcher.cs` lines 96-105 (C5)
- `src/ServiceConnect/Services/ConsumeContext.cs` lines 106-126, 149-171 (C9)

**Tests:**
- Project: `src/ServiceConnect.UnitTests/`
- Coverage: attempted header spoofing of reserved keys does not override; reply with forged `FullTypeName` header for a non-registered type is rejected; reply with mismatched `destinationAddress` is rejected.

- [ ] Follow the **Ground rules workflow** above for this group.
- [ ] Commit message area: `security` or `dispatcher`. Ref footer: `Refs: C1, C5, C9, H7`.

---

## Task 2 — G2: Redelivery bugs (C2 + C4)

**Issues:** C2, C4

**Files to read first:**
- `src/ServiceConnect.Interfaces/Headers/HeaderDecoder.cs` lines 22-24 (C2)
- `src/ServiceConnect/Services/Processors/ReplyProcessor.cs` line 22 (C2 call site)
- `src/ServiceConnect/Services/Processors/StreamProcessor.cs` lines 68, 92, 105 (C2 call sites)
- `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs` lines 340-348 (C4)
- `src/ServiceConnect.Client.RabbitMQ/MessageAuditPublisher.cs` line 39 (C4)

**Tests:**
- Project: `src/ServiceConnect.UnitTests/`
- Coverage: `HeaderDecoder.Decode` returns null/fallback for unsupported types without throwing; audit publish failure after successful handler execution results in message ack (not requeue).

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `rabbitmq` / `interfaces`. Refs: `C2, C4`.

---

## Task 3 — G3: Dispatcher correctness (C3 + C6)

**Issues:** C3, C6

**Files to read first:**
- `src/ServiceConnect/Services/Processors/DefaultProcessManagerPropertyMapper.cs` lines 18-24 (C3)
- `src/ServiceConnect/Services/MessageDispatcher.cs` lines 167-176 (C6)

**Tests:**
- Project: `src/ServiceConnect.UnitTests/`
- Coverage: saga property mapper throws `ArgumentException` on nested `x => x.Customer.Id`-style expressions; `NotHandled` returns a distinct state that the consumer host can act on (log / route to DLQ per config).

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `dispatcher` / `saga`. Refs: `C3, C6`.

---

## Task 4 — G4: In-memory 2-day expiry (C7 + C8)

**Issues:** C7, C8

**Files to read first:**
- `src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs` lines 36, 196 (C7)
- `src/ServiceConnect.Persistence.InMemory/CacheProvider.cs` lines 155-163 (C7)
- `src/ServiceConnect.Persistence.InMemory/InMemoryAggregatorPersistor.cs` lines 28, 172 (C8)

**Tests:**
- Project: `src/ServiceConnect.UnitTests/`
- Coverage: saga state updated just before the 2-day window does not evaporate (either no absolute expiry, or refresh-on-write); aggregator append refreshes its buffer.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `persistence-inmem`. Refs: `C7, C8`.

---

## Task 5 — G5: Process-manager delete concurrency (C10)

**Issues:** C10

**Files to read first:**
- `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs` lines 240-241
- `src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs` lines 285-286

**Tests:**
- Project: `src/ServiceConnect.UnitTests/` (InMemory)
- Project: `src/ServiceConnect.EndToEndTests/` (Mongo — integration; `sg docker -c`)
- Coverage: concurrent update + delete — delete with stale version throws `ConcurrencyException`; delete with matching version succeeds.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `persistence`. Refs: `C10`.

---

## Task 6 — G6: Outbound trace + correlation (C11 + C12)

**Issues:** C11, C12

**Files to read first:**
- `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs` lines 46-54, 140-148 (C11)
- `src/ServiceConnect.Client.RabbitMQ/Producer.cs` lines 466-483 (C11 + C12)
- `src/ServiceConnect/Bus.cs` lines 468-483 (C12)
- `src/ServiceConnect/Services/ConsumeContext.cs` lines 93-95 (C12 consume side)

**Tests:**
- Project: `src/ServiceConnect.UnitTests/`
- Coverage: outbound headers contain injected `traceparent` and the `HeaderKeys.CorrelationId` value from the message; consume side reads the header back correctly.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `telemetry` / `rabbitmq`. Refs: `C11, C12`.

---

## Task 7 — G7: Dispatcher messageType contract (H2)

**Issues:** H2

**Files to read first:**
- `src/ServiceConnect/Services/MessageDispatcher.cs` lines 61-138

**Tests:**
- Project: `src/ServiceConnect.UnitTests/`
- Coverage: `Dispatch(messageType, ...)` with a valid `messageType` argument resolves handlers even when the `FullTypeName` header is absent.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `dispatcher`. Refs: `H2`.

---

## Task 8 — G8: Scoped inbound pipeline + validation (H3 + H4 + M12)

**Issues:** H3, H4, M12

**Files to read first:**
- `src/ServiceConnect/Services/MessageDispatcher.cs` lines 26-57, 179-192 (H3)
- `src/ServiceConnect/ServiceCollectionExtensions.cs` lines 41, 150-163 (H3, M12)
- `src/ServiceConnect/Services/FilterPipeline.cs` lines 10, 45-53 (H4)
- `src/ServiceConnect/ServiceCollectionExtensions.cs` line 61 (H4)

**Tests:**
- Project: `src/ServiceConnect.UnitTests/`
- Coverage: per-message middleware resolved from a scoped provider (lifetime of transient middleware is per-message, not per-bus); per-message filter resolution is scope-aware; startup validator rejects a scoped inbound middleware registered as singleton.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `di` / `dispatcher`. Refs: `H3, H4, M12`.

---

## Task 9 — G9: Handler double-registration (H5)

**Issues:** H5

**Files to read first:**
- `src/ServiceConnect/ServiceCollectionExtensions.cs` lines 208-231
- `src/ServiceConnect/Services/Processors/HandlerProcessor.cs` lines 39-44

**Tests:**
- Project: `src/ServiceConnect.UnitTests/`
- Coverage: a handler pre-registered as transient or scoped is not re-added by `RegisterHandlerType`; a single message dispatches the handler exactly once.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `di`. Refs: `H5`.

---

## Task 10 — G10: Reply/Stream processor filter coverage (H6)

**Issues:** H6

**Files to read first:**
- `src/ServiceConnect/Services/MessageDispatcher.cs` lines 80-92, 133-134
- `src/ServiceConnect/Services/Processors/ReplyProcessor.cs` lines 11-33
- `src/ServiceConnect/Services/Processors/StreamProcessor.cs` lines 57-155

**Tests:**
- Project: `src/ServiceConnect.UnitTests/`
- Coverage: reply dispatch invokes the same `ExecuteBeforeConsumingFiltersAsync`/middleware chain as a normal consume; stream-complete dispatch likewise.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `dispatcher`. Refs: `H6`.

---

## Task 11 — G11: Helper channel publisher confirms (H1)

**Issues:** H1

**Files to read first:**
- `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs` lines 102-106
- `src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs` lines 51-56, 102-106
- `src/ServiceConnect.Client.RabbitMQ/MessageAuditPublisher.cs` lines 38-39

**Tests:**
- Project: `src/ServiceConnect.EndToEndTests/`
- Coverage: retry/error/audit publishes are awaited to confirmation (not just to enqueue). Integration test needed because publisher confirms are a broker behaviour.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `rabbitmq`. Refs: `H1`.

---

## Task 12 — G12: Transport edge-case hardening (H8 + H14 + M23)

**Issues:** H8, H14, M23

**Files to read first:**
- `src/ServiceConnect.Client.RabbitMQ/Producer.cs` lines 328-350 (H8)
- `src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs` lines 38-47 (H14)
- `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs` lines 77-79 (M23)

**Tests:**
- Project: `src/ServiceConnect.UnitTests/`
- Coverage: `SendBytesAsync("")` throws `ArgumentException`; malformed `RetryCount` header routes to error queue (not back to 0); prefetch value of type `long` / `ushort` / `string` does not throw.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `rabbitmq`. Refs: `H8, H14, M23`.

---

## Task 13 — G13: Consumer cancellation & lifecycle (H9 + H10 + H11 + H12)

**Issues:** H9, H10, H11, H12

**Files to read first:**
- `src/ServiceConnect.Client.RabbitMQ/Consumer.cs` lines 37-45, 81, 142-184 (H9, H11, H12)
- `src/ServiceConnect.Client.RabbitMQ/Connection.cs` lines 65-77 (H9)
- `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs` line 111 (H10)

**Tests:**
- Project: `src/ServiceConnect.EndToEndTests/` (lifecycle/cancellation needs a real broker)
- Coverage: cancellation token passed to `StartConsumingAsync` cancels channel creation; a cancelled startup token does not kill subsequent delivery callbacks; partial consumer startup cleans up already-started hosts; externally-supplied connection is NOT disposed by `Consumer.DisposeAsync`.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `rabbitmq`. Refs: `H9, H10, H11, H12`.

---

## Task 14 — G14: Producer heartbeat (H20)

**Issues:** H20

**Files to read first:**
- `src/ServiceConnect.Client.RabbitMQ/Producer.cs` line 115
- `src/ServiceConnect.Client.RabbitMQ/Connection.cs` lines 47-50
- `src/ServiceConnect.Client.RabbitMQ/ConnectionFactoryBuilder.cs` lines 21, 38-39

**Tests:**
- Project: `src/ServiceConnect.UnitTests/`
- Coverage: `ConnectionFactoryBuilder.Build` is called with the configured heartbeat from `ClientSettings` on the producer path (assert via mock/observer on the builder call).

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `rabbitmq`. Refs: `H20`.

---

## Task 15 — G15: Aggregator flush double-dispatch (H13)

**Issues:** H13

**Files to read first:**
- `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs` lines 169-173

**Tests:**
- Project: `src/ServiceConnect.UnitTests/`
- Coverage: cancellation between execute and remove-snapshot does not cause the same batch to flush twice on the next tick.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `processors`. Refs: `H13`.

---

## Task 16 — G16: Process-manager OCC side-effect split (H16)

**Issues:** H16

**Files to read first:**
- `src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs` lines 28-32 (the acknowledgement comment), 74-94, 126-158

**Mini-plan note:** this is the largest High-severity change. The mini-plan step MUST explicitly propose the split boundary (what runs once vs. what is retry-safe) and get explicit user approval before implementation. If the split requires changes to `IProcessHandler` or the `IMessageHandler` contract, flag that and stop for a separate design call.

**Tests:**
- Project: `src/ServiceConnect.UnitTests/`
- Coverage: on forced `ConcurrencyException`, the state-write is retried but the handler body (and any side-effecting work) is not invoked a second time.

- [ ] Follow the **Ground rules workflow** with the above mini-plan caveat.
- [ ] Commit area: `processors` / `saga`. Refs: `H16`.

---

## Task 17 — G17: InMemory lifecycle + clone semantics (H17 + H18 + H19)

**Issues:** H17, H18, H19

**Files to read first:**
- `src/ServiceConnect.Persistence.InMemory/InMemoryAggregatorPersistor.cs` lines 8-21, 41, 57-60, 77-80 (H17, H19)
- `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs` line 11 (H18)
- `src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs` lines 219, 261-265 (H19)
- `src/ServiceConnect.Persistence.InMemory/CacheProvider.cs` line 62 (H19)

**Tests:**
- Project: `src/ServiceConnect.UnitTests/`
- Coverage: disposing a DI container disposes `InMemoryAggregatorPersistor` and `InMemoryPersistenceState` (timers unregistered, RWSL disposed); mutating a nested collection on a saga object after persist does not mutate stored state on subsequent reads.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `persistence-inmem`. Refs: `H17, H18, H19`.

---

## Task 18 — G18: Mongo PM Version restore (H21)

**Issues:** H21

**Files to read first:**
- `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs` lines 188, 199, 205, 221

**Tests:**
- Project: `src/ServiceConnect.EndToEndTests/` (Mongo — integration)
- Coverage: on any failure path (including cancellation between pre-guard and write), the caller's `Version` property is not mutated. Use a local copy for the write; only propagate on confirmed success.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `persistence-mongo`. Refs: `H21`.

---

## Task 19 — G19: Endpoint & queue-mapping validation (M1 + M2)

**Issues:** M1, M2

**Files to read first:**
- `src/ServiceConnect/Bus.cs` lines 117-127 (M1)
- `src/ServiceConnect/Configuration/QueueConfiguration.cs` lines 48-64, 118-132 (M2)

**Tests:** Medium/Low — rely on existing suite (spec Q3 answer B).

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `bus` / `config`. Refs: `M1, M2`.

---

## Task 20 — G20: Type identity fixes (M3 + M4 + M20)

**Issues:** M3, M4, M20

**Files to read first:**
- `src/ServiceConnect/Configuration/QueueConfiguration.cs` lines 27, 40 (M3)
- `src/ServiceConnect/Services/MessageTypeRegistry.cs` lines 30-36 (M4)
- `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs` lines 59, 122, 186, 232, 252, 269-271 (M20)

**Tests:** Medium — existing suite.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `interfaces` / `persistence-mongo`. Refs: `M3, M4, M20`.

---

## Task 21 — G21: Exchange name sanitiser (M5)

**Issues:** M5

**Files to read first:**
- `src/ServiceConnect.Client.RabbitMQ/Producer.cs` line 226
- `src/ServiceConnect/Bus.cs` consumer binding helpers (search for `FullName` + `.Replace(".", string.Empty)`)

**Tests:** Medium — existing suite.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `rabbitmq`. Refs: `M5`.

---

## Task 22 — G22: Options/headers/context immutability (M8 + M9 + M10 + M11)

**Issues:** M8, M9, M10, M11

**Files to read first:**
- `src/ServiceConnect.Interfaces/Handlers/IMessageHandler.cs` line 15 (M8)
- `src/ServiceConnect.Interfaces/ProcessManagers/IProcessHandler.cs` line 18 (M8)
- `src/ServiceConnect.Interfaces/Options/PublishOptions.cs` lines 6-17 (M9)
- `src/ServiceConnect.Interfaces/Options/SendOptions.cs` line 11 (M10)
- `src/ServiceConnect.Interfaces/Options/PublishOptions.cs` line 11 (M10)
- `src/ServiceConnect.Interfaces/Bus/OutgoingEventArgs.cs` lines 16-26 (M11)

**Mini-plan note:** M9 conversion to `readonly record struct` is a **breaking change** to the public API surface. In the mini-plan, flag whether existing call sites need updating; if so, include those edits in the same group.

**Tests:** Medium — existing suite; compile-time enforces M8/M9/M10.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `interfaces`. Refs: `M8, M9, M10, M11`.

---

## Task 23 — G23: Telemetry (M6 + M7)

**Issues:** M6, M7

**Files to read first:**
- `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs` lines 140-153, 193-208
- `src/ServiceConnect/Services/ConsumeContext.cs` lines 47-48

**Tests:** Medium — existing suite; optional single-assertion unit test for `ExtractTraceIdAndState` against a `ReadOnlyDictionary` input if quick.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `telemetry`. Refs: `M6, M7`.

---

## Task 24 — G24: Timeout store correctness (M13 + M14 + M15 + M16 + M17)

**Issues:** M13, M14, M15, M16, M17

**Files to read first:**
- `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs` lines 71-99 (M13)
- `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs` lines 112-117, 173-207, 278-280 (M14, M15, M16)
- `src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs` line 100 (M17)

**Tests:** Medium — existing suite. If a correctness change risks regression, add a targeted integration test (call it out in the mini-plan).

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `persistence`. Refs: `M13, M14, M15, M16, M17`.

---

## Task 25 — G25: Mongo timeout lease reaper (M18)

**Issues:** M18

**Files to read first:**
- `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs` lines 28, 110, 260-266

**Mini-plan note:** this adds a new background worker. In the mini-plan, propose the hosting approach (`IHostedService` registered with DI? polling interval? shutdown coordination?). Defer implementation until user has approved the shape.

**Tests:** This is a new behaviour — add one integration test (crashed handler → reaper recovers the lease within N seconds). Justify the exception to the "Medium = existing suite only" rule in the mini-plan.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `persistence-mongo`. Refs: `M18`.

---

## Task 26 — G26: Mongo TLS settings (M19)

**Issues:** M19

**Files to read first:**
- `src/ServiceConnect.Persistence.MongoDb/MongoClientFactory.cs` lines 50-63

**Tests:** Medium — existing suite.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `persistence-mongo`. Refs: `M19`.

---

## Task 27 — G27: CacheProvider semantics (M21 + M22)

**Issues:** M21, M22

**Files to read first:**
- `src/ServiceConnect.Persistence.InMemory/CacheProvider.cs` lines 68-77, 83-93, 125-140, 184-208

**Tests:** Medium — existing suite, but unit tests for cache semantics are cheap — add them if trivial.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `persistence-inmem`. Refs: `M21, M22`.

---

## Task 28 — G28: Assembly scan + default options (L1 + L2)

**Issues:** L1, L2

**Files to read first:**
- `src/ServiceConnect/ServiceConnectBuilder.cs` lines 33-37 (L1)
- `src/ServiceConnect.Interfaces/Options/RequestOptions.cs` line 16 (L2)

**Tests:** Low — existing suite.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `di` / `interfaces`. Refs: `L1, L2`.

---

## Task 29 — G29: Stream contract + exception ctors (L3 + L4)

**Issues:** L3, L4

**Files to read first:**
- `src/ServiceConnect/Services/IO/ReadOnlyMemoryStream.cs` lines 14-16 (L3)
- `src/ServiceConnect/Services/IO/ReadOnlySequenceStream.cs` lines 17-19 (L3)
- `src/ServiceConnect.Interfaces/Exceptions/ConcurrencyException.cs` and siblings in that folder (L4)

**Tests:** Low — existing suite.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `interfaces`. Refs: `L3, L4`.

---

## Task 30 — G30: Log guards on hot paths (L5)

**Issues:** L5

**Files to read first:**
- `src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs` line 89
- `src/ServiceConnect/Services/Processors/AggregatorRegistry.cs` line 44
- `src/ServiceConnect/Services/Processors/StreamHandlerRegistry.cs` line 40
- `src/ServiceConnect/Services/Processors/MessageHandlerRegistry.cs` line 41

**Tests:** Low — existing suite.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `logging`. Refs: `L5`.

---

## Task 31 — G31: Audit routing key configurable (L6)

**Issues:** L6

**Files to read first:**
- `src/ServiceConnect.Client.RabbitMQ/MessageAuditPublisher.cs` line 39

**Tests:** Low — existing suite.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `rabbitmq`. Refs: `L6`.

---

## Task 32 — G32: IBus PublishRequestAsync param order (L7)

**Issues:** L7

**Mini-plan note:** this is a breaking API change. The mini-plan MUST list every call site (internal + example projects) that needs updating; all edits go in the same commit.

**Files to read first:**
- `src/ServiceConnect.Interfaces/Bus/IBus.cs` lines 13-41
- all call sites of `PublishRequestAsync` (`rg 'PublishRequestAsync' src/ examples/`)

**Tests:** Low — existing suite must still compile and pass.

- [ ] Follow the **Ground rules workflow**.
- [ ] Commit area: `interfaces`. Refs: `L7`.

---

## Task 33: Final verification

- [ ] **Step:** Run full unit suite

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --nologo`
Expected: all tests pass (pass count should be higher than the baseline from Task 0 by the number of tests added across C/H groups).

- [ ] **Step:** Run full integration suite

Run: `sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --nologo'`
Expected: all tests pass.

- [ ] **Step:** Verify consolidated doc progress

Read `consolodated-issues/2026-04-21-consolidated-issues.md` and confirm the progress line reads:
```
**Progress:** Critical 12/12 · High 20/20 · Medium 23/23 · Low 7/7
```

Every issue heading should start with `[x] (commit: <sha>)`.

- [ ] **Step:** Report to user

Present: total commits made (expected 33 — one pre-work + 32 groups), total tests added (sum across C/H groups), any deferred items (should be none by default).

- [ ] **Step:** Stop and await user decision on PR creation

The spec leaves PR creation out of scope; ask the user whether to create a PR now, or leave the branch as-is for them to review.

---

## Self-review checklist

- [x] **Spec coverage:** all 62 issues map to a group in Tasks 1-32; pre-work covered in Task 0; exit check in Task 33.
- [x] **No placeholders:** each task cites exact files and line ranges from the consolidated doc; no "TBD" or "implement later" — the per-group mini-plan step is an explicit workflow gate, not a placeholder.
- [x] **Type consistency:** no code is prescribed across tasks (per the orchestration-plan approach), so type drift across tasks is not a risk.
- [x] **Scope discipline:** each task covers one commit; the mini-plan gate enforces no cross-group drift.
- [x] **Workflow consistency:** every group task references the "Ground rules" workflow, so the checkpoints are identical across G1-G32.
