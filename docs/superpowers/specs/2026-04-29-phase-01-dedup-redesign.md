# Phase 01 — Dedup filter redesign (spec)

**Date:** 2026-04-29
**Branch:** `v7-clean-architecture` (single branch — this work lands here, not on a fork)
**Phase doc:** [`consolidated-issues/phases/phase-01-dedup-filter-redesign.md`](../../../consolidated-issues/phases/phase-01-dedup-filter-redesign.md)
**Status:** approved by user, ready for implementation plan

## Background

The Phase 1 backlog catalogues five Critical, three High, one Medium and ten Low findings against `ServiceConnect.Filters.MessageDeduplication`. Verification on this branch (2026-04-29) confirmed every one is still real:

| ID | Defect | File / line |
|---|---|---|
| C1 | Incoming filter never inserts | [IncomingDeduplicationFilter.cs:44](../../../src/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilter.cs#L44) |
| C2 | Producer-side insert blocks legitimate broker redeliveries | [OutgoingDeduplicationFilter.cs:45](../../../src/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilter.cs#L45) |
| C3 | InMemory `Cache` is `static readonly` | [MessageDeduplicationPersistorInMemory.cs:15](../../../src/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorInMemory.cs#L15) |
| C4 | Read-then-write TOCTOU + non-unique Mongo `_id` index | [MessageDeduplicationPersistorMongoDb.cs:73-93](../../../src/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorMongoDb.cs#L73-L93), [MessageDeduplicationPersistorInMemory.cs:17-28](../../../src/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorInMemory.cs#L17-L28) |
| C5 | Outgoing filter crashes on missing/malformed `MessageId` | [OutgoingDeduplicationFilter.cs:40](../../../src/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilter.cs#L40) |
| H5, H28, H29, M39, L1–L10 | (see phase doc) | (see phase doc) |

## Decisions (locked during brainstorming)

1. **Q1 — Backwards compatibility:** v7 breaking redesign. No shims, no deprecation period.
2. **Q2 — Post-handler hook:** add a new pipeline stage `OnConsumedSuccessfully` that runs only when the handler succeeded. (Not a `ConsumeResult` argument bolted onto `AfterConsumingFilters` — that would force every existing filter to consume a new parameter.)
3. **Q-rescope (the load-bearing one):** **delete the `ServiceConnect.Filters.MessageDeduplication` project entirely.** The library will not ship a dedup implementation. Users build their own filters using the public `IFilter` API and the new `OnConsumedSuccessfully` stage; we ship a worked sample to teach the pattern.
4. **Q-rescope-1 — `OnConsumedSuccessfully` stage stays in scope:** without it, any user-written dedup filter is broken in the same way C1/C2 are broken today.
5. **Q-rescope-2 — Sample location:** rename `examples/MessageDeduplication/` → `examples/CustomFilterAndMiddleware/`. The sample teaches both extension points (filter + middleware) in one place.

The rescope makes Q3, Q4, Q5 (originally about reshaping the persistor interface, the `DisableMsgExpiry` flag, and Mongo client ownership) moot — there is no longer a persistor or a hosted service to argue about.

## Scope

### Deletions

- `src/ServiceConnect.Filters.MessageDeduplication/` (entire project, csproj, all sources, `obj/`, `bin/`).
- `src/ServiceConnect.UnitTests/Filters/MessageDeduplication/` (5 test files):
  - `OutgoingFilterTests.cs`
  - `MessageDeduplicationPersistorInMemoryTests.cs`
  - `IncomingFilterTests.cs`
  - `AddMessageDeduplicationFilterTests.cs`
  - `DeduplicationCleanupHostedServiceTests.cs`
- `examples/MessageDeduplication/` (replaced by `examples/CustomFilterAndMiddleware/`).
- `website/src/content/docs/reference/filters/messagededuplication.mdx`.
- The project entry for `ServiceConnect.Filters.MessageDeduplication` in `ServiceConnect.slnx`.
- The sidebar entry at `website/astro.config.mjs:127` (`'Message Deduplication'`).

### Additions

1. **`OnConsumedSuccessfully` pipeline stage** in `ServiceConnect` core:
   - `IPipelineConfiguration.OnConsumedSuccessfullyFilters` — new `IList<Type>` collection.
   - `IFilterPipeline.ExecuteOnConsumedSuccessfullyFiltersAsync(envelope, ct)` — new method.
   - `FilterPipeline.ExecuteOnConsumedSuccessfullyFiltersAsync(...)` — implementation routes through the existing private `ExecuteFiltersAsync` helper.
   - `ServiceConnectBuilder.AddOnConsumedSuccessfullyFilter<T>()` where `T : class, IFilter`.
   - `MessageDispatcher.DispatchAsync` invokes the new stage in the success path before returning.

2. **`examples/CustomFilterAndMiddleware/`** (rewritten from `examples/MessageDeduplication/`):
   - Three projects: `Contracts`, `Sender`, `Consumer`.
   - Filter side: `IDedupePersistor` + `InMemoryDedupePersistor` + `DedupeIncomingFilter` + `DedupeOnSuccessFilter`.
   - Middleware side: `LoggingTimingMiddleware` (`IMessageProcessingMiddleware`).
   - Sender publishes a few `OrderPlaced` messages, including one deliberate duplicate `MessageId` and one deliberately-induced redelivery.
   - `README.md`, `run.sh`, `run.ps1`, `.sln` updated.

### Documentation updates

- `website/src/content/docs/reference/filters/ifilter.mdx` — add a fourth pipeline-stage entry for `OnConsumedSuccessfully` with the semantics in [§ Pipeline-stage semantics](#pipeline-stage-semantics) below. The page already enumerates `Outgoing` / `BeforeConsuming` / `AfterConsuming` (lines 40–92). The page also contains a `DuplicateDetectionFilter` worked example (lines 100–160) that records the id from a `BeforeConsuming` filter — exactly the C2 anti-pattern we are deleting the package to escape. **Update the example to use the new on-success stage** (split into a `Before` check and an `OnConsumedSuccessfully` record).
- `website/src/content/docs/learn/operations/idempotency.mdx` — rewrite the "infrastructure-level belt-and-braces" section. Lead with handler-side idempotency. Describe the IFilter pattern using the new on-success stage. Link to the new sample. Call out the trade-offs that the deleted package was hiding (TOCTOU under contention, cross-process persistor needs, broker-redelivery vs duplicate-publish are different problems).
- `website/src/content/docs/reference/configuration/ipipelineconfiguration.mdx` — add the new `OnConsumedSuccessfullyFilters` collection wherever the existing `BeforeConsumingFilters`/`AfterConsumingFilters` collections are listed.
- `website/src/content/docs/learn/messaging-patterns/filters.mdx` — verify it doesn't enumerate the stages independently; if it does, update.
- `website/src/content/docs/samples.mdx` — replace the `### MessageDeduplication` block (lines 99–106) with `### CustomFilterAndMiddleware`.
- `website/src/content/docs/releases.mdx` — v7 release-notes entry: `ServiceConnect.Filters.MessageDeduplication` removed (breaking; migration pointer to the new sample); `OnConsumedSuccessfully` pipeline stage added (additive).
- `README.md` (repo root) — search for and update any `MessageDeduplication` mentions.
- `examples/README.md` — rename the `MessageDeduplication` entry to `CustomFilterAndMiddleware`; refresh description.

## Pipeline-stage semantics

`OnConsumedSuccessfully` filters run synchronously inside `MessageDispatcher.DispatchAsync` after the handler chain returns and before the dispatcher's `return`. They run **only** when:

- The chain returned `result.Success == true`, **and**
- `result.NotHandled == false` (a real processor matched the message — the no-handler-found path acks but does not invoke on-success filters).

A filter throwing here propagates into the dispatcher's existing `catch (Exception ex)` block, which produces `Success = false` (so the broker nacks/redelivers — for a dedup filter, that means the persistor write failed and the next delivery should retry the handler). `FilterAction.Stop` halts further on-success filters but does **not** flip `result.Success` to false (the consumption already succeeded; the `Stop` is a "no further on-success work needed" signal).

Filters resolve via `ConsumeScopeAccessor.Current` — same DI scope as the handler — matching the existing pattern in `FilterPipeline.ExecuteFiltersAsync`.

`AfterConsumingFilters` continues to run from the dispatcher's `finally` (existing behaviour, unchanged) — it observes both success and failure paths.

### Where the call site goes

In [`MessageDispatcher.DispatchAsync`](../../../src/ServiceConnect/Services/MessageDispatcher.cs), the chain-result line ([currently line 158](../../../src/ServiceConnect/Services/MessageDispatcher.cs#L158)) becomes:

```csharp
var result = await chain(messageBytes, type!, message, headers, envelope, cancellationToken).ConfigureAwait(false);

if (result.Success && !result.NotHandled)
{
    await _filterPipeline.ExecuteOnConsumedSuccessfullyFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
}

return result;
```

Inside the existing `try`. A throw here flows into the existing `catch` and turns the result into `Success = false`. The `finally` (line 173) is unchanged — `ExecuteAfterConsumingFiltersAsync` still runs for both branches.

## Sample design

Layout under `examples/CustomFilterAndMiddleware/`:

```
README.md
run.sh
run.ps1
CustomFilterAndMiddleware.sln
src/
  ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts/
    OrderPlaced.cs
  ServiceConnect.Examples.CustomFilterAndMiddleware.Sender/
    Program.cs
  ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/
    Program.cs
    OrderPlacedHandler.cs
    Filters/
      IDedupePersistor.cs        // Task<bool> TryInsertAsync(messageId, expiry, ct)
      InMemoryDedupePersistor.cs // ConcurrentDictionary.TryAdd, propagates the bool
      DedupeIncomingFilter.cs    // BeforeConsuming: persistor.Contains? Stop : Continue
      DedupeOnSuccessFilter.cs   // OnConsumedSuccessfully: persistor.TryInsertAsync; throw if false
    Middleware/
      LoggingTimingMiddleware.cs // IMessageProcessingMiddleware: log entry/exit/elapsed
```

Sender behaviour: publishes three `OrderPlaced` messages and exercises three observable scenarios:

1. **Normal message** — handler runs; `DedupeOnSuccessFilter` records the `MessageId`.
2. **Deliberate duplicate `MessageId`** (sender re-publishes the same id) — `DedupeIncomingFilter` sees the id is recorded and returns `Stop`; the handler does not run; the on-success filter does not fire.
3. **Broker redelivery after handler crash** — the consumer's handler throws on the first delivery (broker nack-with-requeue), then succeeds on the redelivery. On the failed attempt the on-success filter does **not** fire (because the dispatcher returns `Success = false`); on the redelivery the incoming filter sees the id is not recorded, the handler runs, and the on-success filter records it. This is the scenario the deleted package's producer-side insert silently dropped (C2 in the phase doc).

Consumer wire-up (the bit users will copy):

```csharp
services.AddSingleton<IDedupePersistor, InMemoryDedupePersistor>();
services.AddTransient<DedupeIncomingFilter>();
services.AddTransient<DedupeOnSuccessFilter>();
services.AddTransient<LoggingTimingMiddleware>();

services.AddServiceConnect(builder =>
{
    builder.UseRabbitMQ(/* ... */);
    builder.AddBeforeConsumingFilter<DedupeIncomingFilter>();
    builder.AddOnConsumedSuccessfullyFilter<DedupeOnSuccessFilter>();
    builder.AddMessageProcessingMiddleware<LoggingTimingMiddleware>();
});
```

Two simplifications vs. the old example:
1. **No Mongo.** Sample needs only RabbitMQ. The README has a "scaling out across replicas" appendix with a code skeleton for a Mongo persistor (unique index + `DuplicateKey` exception → `TryInsertAsync` returns `false`).
2. **No `IOptions`-style config.** Persistor expiry is a hard-coded constant. Production guidance points to the operations idempotency page.

README sections: what this demonstrates → how to run → filter walkthrough (why two filters? why on-success?) → middleware walkthrough (when filter vs middleware?) → production caveats (process restart, cross-replica, TOCTOU, "handler-side idempotency is the canonical answer").

## Test strategy

### New on-success stage tests (in `src/ServiceConnect.UnitTests/`)

Add to or alongside the existing `FilterPipeline` test surface:

1. On-success filters fire after a successful chain — `result.Success == true && !result.NotHandled`.
2. On-success filters do **not** fire when the chain threw (handler exception path).
3. On-success filters do **not** fire when `result.NotHandled == true`.
4. `FilterAction.Stop` from one on-success filter halts subsequent ones; `result.Success` stays `true`.
5. Throw from an on-success filter propagates into the dispatcher's `catch`, producing `Success == false` and the exception attached to `result.Exception`.
6. Cancellation via the supplied `CancellationToken` is honoured (the existing `ExecuteFiltersAsync` already calls `ThrowIfCancellationRequested` in the loop — reuse covers this).
7. Filters resolve via `ConsumeScopeAccessor.Current` (parametric over the new stage in the existing scope-resolution test).
8. `ServiceConnectBuilder.AddOnConsumedSuccessfullyFilter<T>()` appends to `IPipelineConfiguration.OnConsumedSuccessfullyFilters`.

### Deletion validation (manual, captured in plan checklist)

- Repo-wide grep for: `MessageDeduplication`, `IncomingDeduplicationFilter`, `OutgoingDeduplicationFilter`, `IMessageDeduplicationPersistor`, `DeduplicationFilterSettings`, `DeduplicationCleanupHostedService`, `PersistorType`, `ProcessedMessage` — confirm no live references in `src/`, `examples/`, `website/`, repo root after deletion.
- `ServiceConnect.slnx` no longer references the project; per-csproj build of every remaining `src/*.csproj` succeeds.

### Sample validation (manual)

- Per-csproj build of the three sample csprojs.
- `run.sh` smoke test: sender publishes, consumer logs handler entry/exit (middleware), consumer blocks the duplicate `MessageId`, consumer blocks the redelivery.
- README double-read end-to-end before merge.

### Build / test safety (recurring reminder)

Per CLAUDE.md, this machine has crashed when running unconstrained whole-solution `dotnet build`/`test`. The cgroup wrapper at `~/.local/bin/dotnet` is the safety net but per-csproj invocations are still preferred. Plan must use:

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj
dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
dotnet test  src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~FilterPipeline|FullyQualifiedName~Builder|FullyQualifiedName~Dispatcher|FullyQualifiedName~OnConsumedSuccessfully"
```

No whole-solution invocations.

## Verification gate before merge

1. All on-success-stage tests pass.
2. Per-csproj build of every project under `src/` and `examples/CustomFilterAndMiddleware/src/` succeeds.
3. Repo-wide grep for the removed identifiers (listed above) is clean.
4. Astro site builds (`npm --prefix website run build`).
5. Sample's `run.sh` is manually smoke-tested against a local RabbitMQ.
6. Code-review pass via `superpowers:requesting-code-review` (per the phase doc's general rollout guidance).

## Rollout

**Single PR.** The deletion removes most of the surface in one move; the new stage is isolated; the sample is in `examples/` (separate CI surface); docs ride alongside. Splitting into "stage addition first, then deletion + sample + docs" forces a second sweep through the same surface and risks the deletion sitting in review while the stage is unused.

The PR commit shape (suggested, plan will refine):

1. Add `OnConsumedSuccessfully` stage (interface + builder + dispatcher + tests).
2. Delete `ServiceConnect.Filters.MessageDeduplication` project + tests + slnx entry.
3. Rewrite `examples/MessageDeduplication/` → `examples/CustomFilterAndMiddleware/`.
4. Update website + READMEs + release notes.

Each step buildable on its own; reviewers can step through the diff sequentially.

## Out of scope

- Other persistor flavours (Mongo, Redis, Postgres) for the sample. The README's "scaling out" appendix points to the pattern; we don't ship more than one runnable persistor.
- Renaming/refactoring `AfterConsumingFilters` semantics. The `finally`-runs-on-both-paths behaviour is preserved as-is.
- Other phases (telemetry, request/reply, Mongo persistence, etc.). This spec is Phase 01 only.
- Public API for an `IConsumeMiddleware` analogue to `IMessageProcessingMiddleware` for the consume-side filter pipeline (was option C in Q2; rejected).

## Open questions

None at spec time. All design questions were settled during brainstorming.
