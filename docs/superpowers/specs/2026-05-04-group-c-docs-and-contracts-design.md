# Group C — Docs & contracts — design

**Status:** approved, awaiting implementation plan
**Source review:** [architecture-review-deep.md](../../../architecture-review-deep.md)
**Roadmap:** [architecture-fix-plan.md](../../../architecture-fix-plan.md) — Group C
**Date:** 2026-05-04

---

## Overview

Group C makes implicit ServiceConnect documentation and contract guarantees explicit. Three independent, additive items, no API breaks:

1. **Delivery-semantics contract** — at-least-once and the persist-vs-ack gap, on the public bus interface and on the website page where readers ask the natural follow-up.
2. **In-memory persistence boundary** — XML docs at the registration site and a one-shot startup warning log so the test/dev-only scope is loud at code-review time *and* at boot.
3. **Pipeline-extension-point choice** — replace prose with a structured comparison table on the page newcomers already land on.

Items are independently shippable; they don't interlock. Single development cycle, three atomic commits.

---

## In scope

| # | Change | Where | Driver |
|---|---|---|---|
| 1a | `<remarks>` block on `IBus` documenting at-least-once delivery + persist-vs-ack gap + idempotency expectation | [src/ServiceConnect.Interfaces/Bus/IBus.cs](../../../src/ServiceConnect.Interfaces/Bus/IBus.cs) | Make the contract explicit at the IDE-popup point |
| 1b | New "The delivery contract" section in `idempotency.mdx` ahead of "Handler-side idempotency (preferred)" | `website/src/content/docs/learn/operations/idempotency.mdx` | Where readers ask "why must my handler be idempotent?" |
| 1c | Cross-link from `error-handling.mdx`'s existing one-line at-least-once mention to the new section | `website/src/content/docs/learn/operations/error-handling.mdx:136` | Single canonical source |
| 2a | Strongly-worded `<remarks>` blocks on `UseInMemoryPersistence` + each in-memory persistor class | `src/ServiceConnect.Persistence.InMemory/**` | Catches users at composition root and at Go-to-Definition |
| 2b | One-shot `Warning`-level startup log emitted from `UseInMemoryPersistence` registration callback | Same | Loud at boot, filterable via standard log config |
| 2c | Unit test asserting the warning emits exactly once at the right level/category | `src/ServiceConnect.Persistence.InMemory.UnitTests/` (or existing test project for in-memory persistence) | Regression guard |
| 3 | Replace one-paragraph "The middleware alternative" with a structured comparison table | `website/src/content/docs/learn/messaging-patterns/filters.mdx:107-111` | Scenario-driven lookup beats prose for binary choice |

## Out of scope (deliberately)

- **Item 4** (`IAggregatorPersistor` factory convention documentation) — moot. Group A removed the factory-ctor convention; param types are now `IHasCorrelationId`. Nothing left to document.
- **Item 5** (v8 breaking-change summary on website) — already landed during Group A in `releases.mdx` ("v8 highlights" section).
- **Item 6** (supported-runtimes section / TLS expectations) — deferred until Group D resolves the TLS-default decision. Picking up the supported-runtimes page now would force a guess at the TLS stance and create a stale section the moment Group D lands.
- **Mermaid / diagram tooling.** Adding a build dependency for one comparison table that's denser as a table anyway. Defer to a future docs-tooling pass triggered by an actual visual subject (request-reply sequence, process-manager state machine, pub/sub topology).
- **Renaming `UseInMemoryPersistence`.** XML docs + startup log already cover the cases; renaming an established extension method during the v8 settle would add churn without proportional benefit.
- **Environment-aware suppression** of the startup warning (e.g. silence in `Development`). The warning is the whole point. `ILogger` category filtering is the standard escape hatch.
- **Per-method `<remarks>` duplication on every `IBus` send/publish method.** One canonical block at the interface level is enough; method-level summaries stay focused on the method's own behaviour.

---

## Item 1 — Delivery-semantics contract

### 1a. `IBus` interface `<remarks>` block

Above the existing `<summary>` on `public interface IBus` ([IBus.cs:5-8](../../../src/ServiceConnect.Interfaces/Bus/IBus.cs#L5)), add an interface-level `<remarks>` covering three points in plain language:

```csharp
/// <summary>
/// The core message bus interface for publishing, sending, and consuming messages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Delivery is at-least-once.</b> A handler may run more than once for the same logical
/// message — either because the broker redelivered it, or because the consumer did. Idempotency
/// is the consumer's responsibility.
/// </para>
/// <para>
/// <b>Persist-vs-ack gap.</b> When a handler returns successfully, the consumer dispatches an
/// acknowledgement to the broker. If the process crashes (or the broker fails over) between
/// handler success and the ack reaching durable broker state, the message redelivers on next
/// startup. Persistence writes (process-manager state, aggregator data, scheduled timeouts) are
/// completed before the ack — so a redelivered message hits a handler whose persisted state may
/// already reflect the prior run.
/// </para>
/// <para>
/// <b>Implication.</b> Either design handlers to be naturally idempotent (look up by a stable
/// business key, reconcile rather than overwrite), or use a deduplication mechanism — the
/// framework ships <c>MessageDeduplication</c> as a filter for this purpose.
/// </para>
/// </remarks>
public interface IBus : IAsyncDisposable
```

**Rationale for placement:** the `<remarks>` is on the `IBus` interface, not on each `SendAsync` / `PublishAsync` method, because the contract is a property of *consumption*, not of *each individual produce call*. Putting it on the interface means it pops in the IDE when users hover the bus they actually use, without duplicating across nine methods.

**Not on `IConsumer`:** `IConsumer` is internal-leaning — most users never see it. The contract belongs at the public face.

### 1b. New section in `idempotency.mdx`

Add a top-level section **`## The delivery contract`** before the existing `## Handler-side idempotency (preferred)` (currently the first section at line 17 of `idempotency.mdx`).

The section restates the same three points as the XML doc but expands them into prose with a worked example. Suggested content:

```markdown
## The delivery contract

ServiceConnect delivers each logical message **at least once**. A handler may run
more than once for the same message — the broker can redeliver, the consumer can
redeliver, and a process crash between handler success and the broker recording
the ack causes a redelivery on next startup.

The framework persists state (process-manager finders, aggregator data, scheduled
timeouts) **before** sending the ack. So when a redelivery happens, your handler
runs against state that may already reflect the prior attempt's effects.

Concretely: a payment handler that calls `chargeCard(...)` and a redelivery
charges the card twice. The framework will not stop this for you — it cannot tell
your business intent from the message bytes. Two strategies for fixing it, in
preference order:

1. **Make the handler naturally idempotent.** Look up by a stable business key
   first; reconcile rather than overwrite. (Most of this page from here on
   documents this approach.)
2. **Deduplicate at the framework boundary.** The shipped `MessageDeduplication`
   filter rejects messages whose `MessageId` it has seen before. Useful when the
   work itself is hard to make idempotent and you want a generic guard.

Both are valid; the first is cheaper and composes better. Idempotent handlers
also survive scenarios deduplication doesn't catch (e.g. a manual replay through
a tool that mints fresh IDs).
```

The existing two sections (Handler-side idempotency, Filter-based deduplication) follow as concrete techniques *for* the contract this section establishes. The "Combining the two" section at the bottom remains unchanged.

### 1c. Cross-link from `error-handling.mdx`

The existing one-line at-least-once mention at `error-handling.mdx:136` (in section "Idempotency is part of error handling") becomes a cross-link. Replace the standalone sentence with a pointer to the new `idempotency.mdx#the-delivery-contract` anchor. The "Idempotency" section header stays; it now has one paragraph that introduces the topic and links out, rather than re-stating the contract in passing.

---

## Item 2 — In-memory persistence test/dev-only marker

### 2a. XML docs

Five places get a `<remarks>` block that names the test/dev scope explicitly:

| Symbol | File |
|---|---|
| `UseInMemoryPersistence` | [src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs](../../../src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs) |
| `InMemoryAggregatorPersistor` | `src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs` |
| `InMemoryProcessManagerFinder` | `src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs` |
| `InMemoryTimeoutStore` | `src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs` |
| `InMemoryPersistenceState` | `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs` |

Same content in each, scaled to the symbol. Example for `UseInMemoryPersistence`:

```csharp
/// <summary>
/// Registers the in-memory persistence implementation with the builder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Intended for development and tests.</b> All state is held in-process in
/// <see cref="InMemoryPersistenceState"/> and is not durable across restarts. This is the
/// right choice for unit and integration tests that need a real persistor without provisioning
/// infrastructure, and for local development.
/// </para>
/// <para>
/// <b>Do not use in production.</b> A process restart loses every in-flight process-manager
/// instance, every pending aggregator group, and every scheduled timeout. Use a durable
/// persistor (e.g. <c>UseMongoDbPersistence</c>) for production deployments.
/// </para>
/// </remarks>
```

The class-level docs catch users who navigate via Go-to-Definition; the extension-method doc catches users at the registration call. Same words, different surface — both are seen by different reading styles.

### 2b. Startup warning log

A one-shot `LogWarning` emitted from inside the `AddRegistration` callback in `UseInMemoryPersistence`. The callback runs once when the bus is built, so the log is one-shot per bus instance.

Concrete shape:

- **Logger category:** `ServiceConnect.Persistence.InMemory` (matches the namespace; users filter via standard log config).
- **Level:** `Warning`.
- **Event ID:** stable numeric ID inside a small `LogEvents` constants class in the in-memory persistence project. Convention: define `InMemoryPersistenceRegistered = 1` as the first entry. This positions the project for future log-event additions without churn.
- **Message:** `"In-memory persistence is registered. State is held in-process and is not durable across restarts. This is intended for development and tests; use a real persistor (e.g. MongoDB) in production."`
- **Implementation:** acquire `ILogger<InMemoryPersistenceState>` (or a per-extension marker type) inside the `AddRegistration` callback, log once.

```csharp
builder.AddRegistration(services =>
{
    services.TryAddSingleton(options);
    services.TryAddSingleton<InMemoryPersistenceState>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<InMemoryPersistenceState>>();
        logger.LogInMemoryPersistenceRegistered();   // source-generated logger method
        return new InMemoryPersistenceState(sp.GetRequiredService<TimeProvider>());
    });
    // ... rest unchanged
});
```

The warning fires the first time `InMemoryPersistenceState` is materialised — i.e. at first message processing or first bus call. That's marginally later than DI registration (so users see it in the host's running logs, not just startup), but it's deterministic and one-shot per bus instance because `InMemoryPersistenceState` is a singleton.

**Source-generated logger method** rather than `logger.LogWarning(...)` directly so the event ID is stable, the message template is enforced at compile time, and there's zero allocation on the hot path (here: not hot, but the convention is project-consistent if other logs follow).

### 2c. Test

One unit test asserting the warning emits exactly once when a bus that includes `UseInMemoryPersistence` is built and first uses persistence. Use a captured-log-records `ILogger<InMemoryPersistenceState>` (e.g. `FakeLogger` from `Microsoft.Extensions.Logging.Testing`, or a custom in-memory `ILogger` if that package isn't already a dependency).

Asserts:
- Exactly one record at level `Warning`.
- Category contains `ServiceConnect.Persistence.InMemory`.
- Event ID matches the `LogEvents.InMemoryPersistenceRegistered` constant.

The test lives in `src/ServiceConnect.UnitTests/Persistence/` alongside the existing in-memory fixtures (`InMemoryProcessManagerFinder*Tests`, `InMemoryTimeoutStoreOptionsTests`, etc.). Suggested filename: `InMemoryPersistenceRegistrationLogTests.cs`.

### Suppression

Standard `Microsoft.Extensions.Logging` filtering applies — anyone who wants to silence the warning sets the `ServiceConnect.Persistence.InMemory` category to `Error` or higher in their `appsettings.json` / log config. **No bespoke "disable" flag** — that would defeat the point of the warning.

---

## Item 3 — Filter-vs-middleware comparison table

### Where

In-place edit of `learn/messaging-patterns/filters.mdx`, replacing the existing one-paragraph "The middleware alternative" section (lines 107–111). Section heading stays as `## The middleware alternative`.

### Shape

Scenario-driven comparison table. Reader scans the left column for their use case and reads across.

```markdown
## The middleware alternative

ServiceConnect also has two middleware hooks — `AddSendMessageMiddleware<T>` and
`AddMessageProcessingMiddleware<T>` — which wrap the whole operation with `next`-delegate
semantics, the way ASP.NET Core middleware does. Filters are stateless inspect/stamp/maybe-stop;
middleware is wrap-the-operation. Pick by scenario:

| You want to… | Use | Why |
|---|---|---|
| Read or stamp a header | Filter | Stateless inspect/produce; that's the whole filter contract |
| Short-circuit the pipeline based on header content | Filter | `FilterAction.Stop` is a first-class outcome |
| Authorise the message based on incoming claims and reject | Filter | Inspect headers, return `FilterAction.Stop` if rejected |
| Wrap the inner pipeline with `try`/`finally` (open a tracing scope, close it) | Middleware | Filters can't observe completion of `next` |
| Time the whole consume operation | Middleware | Need a `Stopwatch` that brackets `next()` |
| Catch exceptions thrown by the inner pipeline | Middleware | Filters return `FilterAction`; they don't see exceptions from `next` |
| Mutate the message body | Middleware | Filters expose body as read-only `ReadOnlyMemory<byte>` |
| Open a unit-of-work, commit on success, rollback on exception | Middleware | Same `try`/`finally` reasoning |

Rule of thumb: **filter when "inspect or stamp, maybe stop" is the whole job; middleware when
you need to wrap the operation.**
```

The section's existing pointer prose around the table (intro paragraph and rule of thumb) is condensed but retained — the table is the lookup, the prose is the heuristic.

The cross-references at the existing `## Reference` section (line 113) stay as-is.

### Verification

Each row is verified at write-time against the actual `IFilter` / `IMessageProcessingMiddleware` / `ISendMessageMiddleware` interfaces. The author opens the three interface files alongside the table draft and checks:

- "Body is read-only on filter" — `IFilter` exposes `ReadOnlyMemory<byte>`? Yes (verified during writing-plans).
- "Filters can't observe `next`" — `IFilter` returns `Task<FilterAction>` with no `next` parameter? Yes.
- "Middleware sees exceptions from `next`" — middleware `InvokeAsync(ctx, next)` allows `try`/`catch (Exception) { … } await next() { … }`? Yes.

If any row turns out to be inaccurate during verification, the row is corrected before the commit lands; this happens at writing-plans time, not later.

---

## Testing strategy

| Item | Verification |
|---|---|
| 1a. `IBus` `<remarks>` | Build pipeline (xmldoc analyzer + `TreatWarningsAsErrors`) catches malformed XML doc syntax; visual review for tone |
| 1b. `idempotency.mdx` new section | Local Astro build (`npm run build` in `website/`) catches broken markdown / cross-link errors |
| 1c. `error-handling.mdx` cross-link | Same Astro build verifies the anchor resolves |
| 2a. XML docs across five symbols | Build pipeline catches malformed XML; visual review for consistency of wording |
| 2b. Startup warning log | One unit test (Item 2c) asserting it fires |
| 2c. Test itself | New test passes; full unit-test sweep stays green |
| 3. Comparison table | Astro build catches markdown errors; per-row accuracy verified at writing-plans time against interface code |

No new automated content tests beyond Item 2c.

---

## Rollout

Three atomic commits on the existing `v7-clean-architecture` branch (consistent with how Group A landed; no separate worktree).

1. **`docs(interfaces): document the at-least-once delivery contract on IBus`** — covers Items 1a + 1b + 1c. `IBus.cs`, `idempotency.mdx`, `error-handling.mdx`. One coherent doc story across code and website; reviewers see it as a single thought.
2. **`feat(persistence-inmemory): warn on registration; document test/dev scope`** — covers Items 2a + 2b + 2c. XML docs across five files, the startup warning log, the unit test. Single commit because the XML wording and the log message are deliberately consistent; splitting would risk drift.
3. **`docs(website): replace filter-vs-middleware prose with comparison table`** — Item 3. `filters.mdx` only.

Each commit independently passes build + tests; can be reviewed and reverted in isolation. After all three land:

- Update `architecture-fix-plan.md` row for Group C from `*pending*` to `*done*`.
- A `docs(architecture): mark Group C done in the fix plan` commit is the close-out.

---

## Risks

- **Tone of the at-least-once `<remarks>`.** Could read as alarming if framed defensively. Mitigation: the wording above frames it as contract first ("delivery is at-least-once") and points at the framework hook (`MessageDeduplication`) and the natural fix (idempotent handlers). Not "warning, your data may be lost" framing.
- **Startup warning log noise in test environments.** Tests using in-memory persistence will see the warning every time a fresh bus is built. Mitigation: standard `ILogger` category filtering at the test-runner config level handles it. Documented at the bottom of `idempotency.mdx` or the in-memory reference page (one-liner: "to silence the warning in tests, set `ServiceConnect.Persistence.InMemory` to `Error`").
- **Comparison table accuracy.** A wrong row reads as authoritative. Mitigation: each row verified against the actual interface contract code at writing-plans time (Section "Verification" above).
- **`<remarks>` content drift over time.** The persist-vs-ack contract is described in the IBus XML doc, in `idempotency.mdx`, and arguably in `error-handling.mdx`. Three places to keep in sync if the contract ever changes. Mitigation: the website page is the canonical narrative; the XML doc is a short summary that points at it implicitly (via its phrasing, not via a hyperlink — XML docs don't render website links portably). If the contract changes, all three update together; no automation guards this, but the contract has been at-least-once since v1 and no change is on any roadmap.

## Rollback

All three commits are pure additions to docs and one new log line. `git revert` of any one commit cleanly removes that item without affecting the other two. No data migrations, no API surface changes, no config schema changes.

---

## Decisions banked from the brainstorm

For audit trail:

- **At-least-once contract location:** XML doc on `IBus` interface only (not per-method, not on `IConsumer`); website extension lives in `idempotency.mdx` (not a new dedicated page, not just `the-bus.mdx`). One canonical source on each side.
- **In-memory marker:** XML docs + startup warning log. **Not** renamed to `UseInMemoryPersistenceForDevelopment` — XML+log is loud enough without breaking the established extension-method name during the v8 settle.
- **Filter-vs-middleware:** comparison table in-place in `filters.mdx`. **Not** a new dedicated page; **not** Mermaid; **not** a flowchart. The decision is a comparison, not a sequence.
- **Mermaid:** deferred to a future docs-tooling pass triggered by an actual visual subject.
- **Item 6 (supported runtimes / TLS):** deferred until Group D's TLS-default decision lands. Picking it up now would force a stale section.
- **Items 4 and 5** (factory convention, v8 release notes summary): out — already resolved or done in Group A.
