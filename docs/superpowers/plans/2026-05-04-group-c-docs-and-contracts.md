# Group C — Docs & Contracts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make ServiceConnect's three implicit doc/contract guarantees explicit — at-least-once delivery, in-memory test/dev scope, filter-vs-middleware choice — without changing any public API.

**Architecture:** Three independent additive items. Item 1 lands as `<remarks>` on `IBus` plus a new section in `idempotency.mdx` and a cross-link from `error-handling.mdx`. Item 2 lands as XML docs across five symbols, plus a one-shot `Warning`-level startup log emitted from the `InMemoryPersistenceState` singleton factory and a unit test that exercises it through `ServiceConnectBuilder`. Item 3 replaces a one-paragraph section in `filters.mdx` with a structured comparison table.

**Tech Stack:** .NET 10, C# 14, xUnit, Moq, `Microsoft.Extensions.Logging` 9.0.0 (source-gen logger via `[LoggerMessage]`), `Microsoft.Extensions.Logging.Testing` 9.0.0 (`FakeLogger<T>`), Astro / Starlight (website).

---

## File structure

| File | Item | Action |
|---|---|---|
| `src/ServiceConnect.Interfaces/Bus/IBus.cs` | 1 | Modify — add `<remarks>` block on interface |
| `website/src/content/docs/learn/operations/idempotency.mdx` | 1 | Modify — add new "The delivery contract" section |
| `website/src/content/docs/learn/operations/error-handling.mdx` | 1 | Modify — replace standalone para with cross-link |
| `src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj` | 2 | Modify — add `Microsoft.Extensions.Logging.Abstractions` package |
| `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceLog.cs` | 2 | Create — source-gen logger partial class + log-event ID |
| `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs` | 2 | Modify — XML doc `<remarks>` + wire log call into the singleton factory |
| `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs` | 2 | Modify — XML doc `<remarks>` |
| `src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs` | 2 | Modify — XML doc `<remarks>` |
| `src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs` | 2 | Modify — XML doc `<remarks>` |
| `src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs` | 2 | Modify — XML doc `<remarks>` |
| `src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj` | 2 | Modify — add `Microsoft.Extensions.Logging.Testing` package |
| `src/ServiceConnect.UnitTests/Persistence/InMemoryPersistenceRegistrationLogTests.cs` | 2 | Create — unit test |
| `website/src/content/docs/learn/messaging-patterns/filters.mdx` | 3 | Modify — replace lines 107–111 |
| `architecture-fix-plan.md` | close | Modify — mark Group C done |

---

## Task 1: At-least-once delivery contract

Single coherent doc story across `IBus.cs` (the contract source-of-truth) and the website (`idempotency.mdx` extension + `error-handling.mdx` cross-link). Lands as one atomic commit.

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Bus/IBus.cs:5-8` — replace the existing single-line summary with a summary + `<remarks>` block.
- Modify: `website/src/content/docs/learn/operations/idempotency.mdx:1-17` — insert a new top-level section before `## Handler-side idempotency (preferred)`.
- Modify: `website/src/content/docs/learn/operations/error-handling.mdx:134-138` — replace the standalone "Idempotency is part of error handling" body with a cross-link paragraph.

- [ ] **Step 1: Add the `<remarks>` block to `IBus`**

Open `src/ServiceConnect.Interfaces/Bus/IBus.cs`. Replace the existing interface-level summary (lines 5-8) with:

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

The rest of the file is unchanged.

- [ ] **Step 2: Verify the `IBus` build is clean**

Run: `dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1`
Expected: 0 errors, 0 warnings. The xmldoc analyzer will flag malformed XML — there should be no malformed XML.

- [ ] **Step 3: Add the new section in `idempotency.mdx`**

Open `website/src/content/docs/learn/operations/idempotency.mdx`. Find line 17 (`## Handler-side idempotency (preferred)`). Insert the following content **above** that line, after whatever frontmatter / intro already exists:

```markdown
## The delivery contract

ServiceConnect delivers each logical message **at least once**. A handler may run
more than once for the same message — the broker can redeliver, the consumer can
redeliver, and a process crash between handler success and the broker recording
the ack causes a redelivery on next startup.

The framework persists state (process-manager finders, aggregator data, scheduled
timeouts) **before** sending the ack. So when a redelivery happens, your handler
runs against state that may already reflect the prior attempt's effects.

Concretely: a payment handler that calls `chargeCard(...)` and then crashes will
charge the card twice on redelivery. The framework will not stop this for you —
it cannot tell your business intent from the message bytes. Two strategies for
fixing it, in preference order:

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

- [ ] **Step 4: Replace the standalone paragraph in `error-handling.mdx`**

Open `website/src/content/docs/learn/operations/error-handling.mdx`. Find lines 134–138 (the section `## Idempotency is part of error handling` and its body):

```markdown
## Idempotency is part of error handling

A message that retries has a non-trivial chance of being redelivered after partial success — the handler did its work, then failed before acknowledging. This is a [Competing Consumers](/ServiceConnect-CSharp/learn/messaging-patterns/competing-consumers/) problem amplified: at-least-once delivery is the default, and "at-least-once with retries" amplifies the duplicate rate.

Design handlers to tolerate being run twice on the same message: upsert instead of insert, check state before acting, use the correlation id or message id as the idempotency key for downstream calls. This is not optional — the retry loop assumes it.
```

Replace with:

```markdown
## Idempotency is part of error handling

A message that retries has a non-trivial chance of being redelivered after partial success — the handler did its work, then failed before acknowledging. The retry loop amplifies the duplicate rate that's inherent to ServiceConnect's at-least-once delivery contract.

For the full delivery contract and the strategies that handle redelivery correctly, see [The delivery contract](/ServiceConnect-CSharp/learn/operations/idempotency/#the-delivery-contract). The short version: handlers must tolerate being run twice on the same message — upsert instead of insert, check state before acting, use the correlation id or message id as the idempotency key for downstream calls. The retry loop assumes it.
```

- [ ] **Step 5: Verify Astro build**

Run:
```bash
cd website && npm run build && cd ..
```

Expected: build succeeds. Astro warns on broken intra-site links; the new `/learn/operations/idempotency/#the-delivery-contract` anchor must resolve.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Interfaces/Bus/IBus.cs \
        website/src/content/docs/learn/operations/idempotency.mdx \
        website/src/content/docs/learn/operations/error-handling.mdx

git commit -m "$(cat <<'EOF'
docs(interfaces): document the at-least-once delivery contract on IBus

Adds a <remarks> block to IBus making three points explicit:

- Delivery is at-least-once; handlers may run more than once.
- Persist-vs-ack gap: persistence writes complete before ack, so a
  redelivery hits a handler whose persisted state may already reflect
  the prior run.
- Either design handlers to be naturally idempotent, or use the shipped
  MessageDeduplication filter.

The website extends idempotency.mdx with a new "The delivery contract"
top section that establishes the same contract with a worked example
(payment handler / chargeCard) before the existing handler-side
idempotency / filter-based deduplication sections, which now read as
techniques for the contract this section establishes.

The standalone at-least-once paragraph in error-handling.mdx becomes a
cross-link to the canonical section, so the contract has one home.

No API changes. Pure documentation.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: In-memory persistence test/dev-only marker

Adds the `Microsoft.Extensions.Logging` dependency to the in-memory persistence project, introduces a source-generated logger with a stable event ID, wires the warning into the `InMemoryPersistenceState` singleton factory inside `UseInMemoryPersistence`, adds explicit `<remarks>` blocks to all five symbols, and covers the wiring with a regression test.

TDD: build the failing test first (it can't compile until the source-gen logger exists), then add the production code, then add the XML docs.

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj` — add `Microsoft.Extensions.Logging.Abstractions` package.
- Modify: `src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj` — add `Microsoft.Extensions.Logging.Testing` package.
- Create: `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceLog.cs` — source-gen logger class.
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs` — wire the log call + add XML doc.
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs` — XML doc.
- Modify: `src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs` — XML doc.
- Modify: `src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs` — XML doc.
- Modify: `src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs` — XML doc.
- Create: `src/ServiceConnect.UnitTests/Persistence/InMemoryPersistenceRegistrationLogTests.cs` — unit test.

- [ ] **Step 1: Add `Microsoft.Extensions.Logging.Abstractions` to InMemory project**

Open `src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj`. Find the `<ItemGroup>` containing `<PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.0" />`. Add a sibling `PackageReference` immediately before or after it:

```xml
<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
```

Final ItemGroup should look like:

```xml
<ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
</ItemGroup>
```

- [ ] **Step 2: Add `Microsoft.Extensions.Logging.Testing` to UnitTests project**

Open `src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`. Find the `<ItemGroup>` containing `<PackageReference Include="Microsoft.Extensions.Logging" Version="9.0.0" />`. Add immediately after:

```xml
<PackageReference Include="Microsoft.Extensions.Logging.Testing" Version="9.0.0" />
```

- [ ] **Step 3: Restore packages**

Run: `dotnet restore src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj && dotnet restore src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`
Expected: both restore cleanly.

- [ ] **Step 4: Write the failing test**

Create `src/ServiceConnect.UnitTests/Persistence/InMemoryPersistenceRegistrationLogTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using ServiceConnect;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class InMemoryPersistenceRegistrationLogTests
{
    [Fact]
    public void UseInMemoryPersistence_OnFirstStateResolution_LogsWarningExactlyOnce()
    {
        var builder = new ServiceConnectBuilder();
        builder.UseInMemoryPersistence();

        var fakeLogger = new FakeLogger<InMemoryPersistenceState>();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<ILogger<InMemoryPersistenceState>>(fakeLogger);
        builder.AdditionalRegistrations[0](services);

        using var sp = services.BuildServiceProvider();
        var first = sp.GetRequiredService<InMemoryPersistenceState>();
        var second = sp.GetRequiredService<InMemoryPersistenceState>();

        Assert.Same(first, second);

        var records = fakeLogger.Collector.GetSnapshot();
        Assert.Single(records);
        Assert.Equal(LogLevel.Warning, records[0].Level);
        Assert.Equal(InMemoryPersistenceLog.InMemoryPersistenceRegisteredEventId, records[0].Id.Id);
        Assert.Contains("In-memory persistence is registered", records[0].Message);
        Assert.Contains("development and tests", records[0].Message);
        Assert.Contains("MongoDB", records[0].Message);
    }
}
```

- [ ] **Step 5: Run the test — expect failure**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryPersistenceRegistrationLogTests" -m:1`
Expected: build error — `InMemoryPersistenceLog` and `InMemoryPersistenceLog.InMemoryPersistenceRegisteredEventId` don't exist yet.

- [ ] **Step 6: Create `InMemoryPersistenceLog.cs`**

Create `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceLog.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Source-generated logger entries emitted by the in-memory persistence package.
/// </summary>
internal static partial class InMemoryPersistenceLog
{
    /// <summary>
    /// Stable event id for the one-shot registration warning.
    /// </summary>
    public const int InMemoryPersistenceRegisteredEventId = 1;

    [LoggerMessage(
        EventId = InMemoryPersistenceRegisteredEventId,
        EventName = "InMemoryPersistenceRegistered",
        Level = LogLevel.Warning,
        Message = "In-memory persistence is registered. State is held in-process and is not durable across restarts. This is intended for development and tests; use a real persistor (e.g. MongoDB) in production.")]
    public static partial void InMemoryPersistenceRegistered(ILogger logger);
}
```

- [ ] **Step 7: Wire the log call into `UseInMemoryPersistence`**

Open `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs`. Find the existing `InMemoryPersistenceState` singleton factory (lines 34-35):

```csharp
services.TryAddSingleton<InMemoryPersistenceState>(sp =>
    new InMemoryPersistenceState(sp.GetRequiredService<TimeProvider>()));
```

Replace it with a multi-line factory that resolves the logger and emits the warning before constructing the state instance:

```csharp
services.TryAddSingleton<InMemoryPersistenceState>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<InMemoryPersistenceState>>();
    InMemoryPersistenceLog.InMemoryPersistenceRegistered(logger);
    return new InMemoryPersistenceState(sp.GetRequiredService<TimeProvider>());
});
```

Add `using Microsoft.Extensions.Logging;` at the top of the file (alongside the existing `using` statements).

- [ ] **Step 8: Run the test — expect pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryPersistenceRegistrationLogTests" -m:1`
Expected: 1 passed.

- [ ] **Step 9: Add the XML doc `<remarks>` to `UseInMemoryPersistence`**

Open `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs`. Find the existing `<summary>` on `UseInMemoryPersistence` (lines 12-19). Replace with:

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
/// <para>
/// At bus build time this method emits a <see cref="LogLevel.Warning"/>-level log under
/// the <c>ServiceConnect.Persistence.InMemory</c> category to surface the test/dev scope at
/// runtime. The warning fires once per bus instance. To silence in test runs, raise the
/// category's minimum level to <see cref="LogLevel.Error"/> via standard
/// <c>Microsoft.Extensions.Logging</c> filter configuration.
/// </para>
/// </remarks>
/// <param name="builder">The ServiceConnect builder.</param>
/// <param name="configure">
/// Optional delegate to customise <see cref="InMemoryPersistenceOptions"/> before registration.
/// When omitted the defaults (e.g. a five-minute lock-lease duration) are used.
/// </param>
```

The class-level `<summary>` (lines 7-9) stays as-is.

- [ ] **Step 10: Add `<remarks>` to `InMemoryPersistenceState`**

Open `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs`. Find the class-level `<summary>` (the doc above `public class InMemoryPersistenceState` or `internal class InMemoryPersistenceState`).

Locate that `<summary>` and append a `<remarks>` block immediately after the closing `</summary>` line:

```csharp
/// <remarks>
/// <b>Intended for development and tests.</b> All state held by this type is in-process and
/// is not durable across restarts. Use <c>UseMongoDbPersistence</c> or another durable
/// persistor for production. <c>UseInMemoryPersistence</c> emits a startup warning when this
/// type is materialised; see that method's remarks for filtering guidance.
/// </remarks>
```

Place it directly after the closing `</summary>` and before the class declaration line.

- [ ] **Step 11: Add `<remarks>` to `InMemoryAggregatorPersistor`**

Open `src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs`. Find the class-level `<summary>`. Append a `<remarks>` block directly after the closing `</summary>`:

```csharp
/// <remarks>
/// <b>Intended for development and tests.</b> Aggregator data is held in-process and is not
/// durable across restarts. Use a durable <see cref="ServiceConnect.Interfaces.IAggregatorPersistor"/>
/// implementation (e.g. the MongoDB persistor) for production.
/// </remarks>
```

- [ ] **Step 12: Add `<remarks>` to `InMemoryProcessManagerFinder`**

Open `src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs`. Find the class-level `<summary>`. Append a `<remarks>` block directly after the closing `</summary>`:

```csharp
/// <remarks>
/// <b>Intended for development and tests.</b> Process-manager state is held in-process and is
/// not durable across restarts; in-flight process-manager instances are lost on restart. Use a
/// durable <see cref="ServiceConnect.Interfaces.IProcessManagerFinder"/> implementation (e.g.
/// the MongoDB finder) for production.
/// </remarks>
```

- [ ] **Step 13: Add `<remarks>` to `InMemoryTimeoutStore`**

Open `src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs`. Find the class-level `<summary>`. Append a `<remarks>` block directly after the closing `</summary>`:

```csharp
/// <remarks>
/// <b>Intended for development and tests.</b> Scheduled timeouts are held in-process and are
/// lost on restart. Use a durable <see cref="ServiceConnect.Interfaces.ITimeoutStore"/>
/// implementation (e.g. the MongoDB timeout store) for production.
/// </remarks>
```

- [ ] **Step 14: Verify the in-memory project + unit tests build clean**

Run:
```bash
dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj -m:1
dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1
```
Expected: both 0 errors, 0 warnings. The XML doc analyzers will catch any malformed `<remarks>` syntax.

- [ ] **Step 15: Run the full unit test sweep to verify no regressions**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore -m:1`
Expected: all tests pass (1177 tests after Task 2 = 1176 baseline + 1 new). If the MongoDB BsonSerializer init-order flake hits, re-run once.

- [ ] **Step 16: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj \
        src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceLog.cs \
        src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs \
        src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs \
        src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs \
        src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs \
        src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs \
        src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
        src/ServiceConnect.UnitTests/Persistence/InMemoryPersistenceRegistrationLogTests.cs

git commit -m "$(cat <<'EOF'
feat(persistence-inmemory): warn on registration; document test/dev scope

In-memory persistence is for development and tests, not production.
That intent has been implicit; this commit makes it explicit at both
compile time (XML docs) and runtime (startup warning log).

XML docs:
- UseInMemoryPersistence and four implementation classes
  (InMemoryPersistenceState, InMemoryAggregatorPersistor,
  InMemoryProcessManagerFinder, InMemoryTimeoutStore) gain <remarks>
  blocks naming the test/dev scope and the production alternative.

Runtime warning:
- A source-generated logger (InMemoryPersistenceLog with EventId 1,
  category ServiceConnect.Persistence.InMemory) emits a Warning-level
  log "In-memory persistence is registered..." once per bus instance,
  fired from the InMemoryPersistenceState singleton factory inside
  UseInMemoryPersistence. The log is filterable via standard
  Microsoft.Extensions.Logging category configuration; raise the
  category's minimum level to Error in test runs to silence.

Adds Microsoft.Extensions.Logging.Abstractions 9.0.0 to the in-memory
persistence package and Microsoft.Extensions.Logging.Testing 9.0.0
(FakeLogger) to the unit-tests project. The new test exercises the
production wiring through ServiceConnectBuilder + AdditionalRegistrations
and asserts the warning emits exactly once across two singleton
resolutions.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Filter-vs-middleware comparison table

In-place edit of one section in `filters.mdx`. Each row is verified against the actual `IFilter` / `IMessageProcessingMiddleware` / `ISendMessageMiddleware` interface contracts before the edit lands.

**Files:**
- Modify: `website/src/content/docs/learn/messaging-patterns/filters.mdx:107-111` — replace the existing "The middleware alternative" section.

- [ ] **Step 1: Verify each row against the actual interfaces**

Open the three interface files:
- `src/ServiceConnect.Interfaces/Bus/IFilter.cs`
- `src/ServiceConnect.Interfaces/Bus/IMessageProcessingMiddleware.cs`
- `src/ServiceConnect.Interfaces/Bus/ISendMessageMiddleware.cs`

Confirm each claim that will appear in the table. Read the actual signatures for:

- `IFilter`: filter return type is `Task<FilterAction>` (no `next` delegate); body parameter type on the consume context type used by filters.
- `IMessageProcessingMiddleware`: invocation signature contains a `next` delegate that returns a `Task`/`ValueTask`; the middleware can wrap with `try`/`finally`.
- `ISendMessageMiddleware`: same pattern, send-side variant.
- Confirm that the filter consume-context exposes the body as `ReadOnlyMemory<byte>` (read-only).

If any of the eight rows below is contradicted by the actual contract, correct the row before writing it. Note the corrections in the commit message if any apply.

The eight rows to verify:

1. "Read or stamp a header" → Filter — verified by `IFilter` taking the consume context and being able to read/write `Headers`.
2. "Short-circuit the pipeline based on header content" → Filter — verified by `FilterAction.Stop` being a returnable value of `FilterAction`.
3. "Authorise the message based on incoming claims and reject" → Filter — same `FilterAction.Stop` mechanism.
4. "Wrap the inner pipeline with `try`/`finally`" → Middleware — verified by middleware seeing `next` and being able to wrap calls to it.
5. "Time the whole consume operation" → Middleware — same wrap-`next` reasoning.
6. "Catch exceptions thrown by the inner pipeline" → Middleware — verified by `IFilter` having no `next` (exceptions inside `next` are not surfaced to filters; middleware can `try`/`catch` around `await next()`).
7. "Mutate the message body" → Middleware — verified by the consume-context body type exposed to filters being `ReadOnlyMemory<byte>` while middleware has access to the mutable headers and can construct a fresh inbound message via the next-delegate seam.
8. "Open a unit-of-work, commit on success, rollback on exception" → Middleware — same `try`/`finally` reasoning.

- [ ] **Step 2: Replace the section**

Open `website/src/content/docs/learn/messaging-patterns/filters.mdx`. Find the existing section starting at line 107:

```markdown
## The middleware alternative

ServiceConnect also has two middleware hooks — `AddSendMessageMiddleware<T>` and `AddMessageProcessingMiddleware<T>` — which wrap the whole operation with `next`-delegate semantics, the way ASP.NET Core middleware does. Filters are the lighter tool: stateless, returning `FilterAction`. Middleware is heavier: full control over the surrounding context, `try`/`finally` around `next()`.

Reach for a filter when "inspect or stamp headers, maybe stop" is the whole job. Reach for middleware when you need to wrap — time the whole operation, open and close a scope around it, catch exceptions from the inner pipeline.
```

Replace those five lines (the heading + two paragraphs) with:

```markdown
## The middleware alternative

ServiceConnect also has two middleware hooks — `AddSendMessageMiddleware<T>` and `AddMessageProcessingMiddleware<T>` — which wrap the whole operation with `next`-delegate semantics, the way ASP.NET Core middleware does. Filters are stateless inspect/stamp/maybe-stop; middleware is wrap-the-operation. Pick by scenario:

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

Rule of thumb: **filter when "inspect or stamp, maybe stop" is the whole job; middleware when you need to wrap the operation.**
```

Leave the surrounding sections (`## Order of execution` above, `## Reference` below) untouched.

- [ ] **Step 3: Verify Astro build**

Run:
```bash
cd website && npm run build && cd ..
```
Expected: build succeeds; the table renders without markdown errors.

- [ ] **Step 4: Commit**

```bash
git add website/src/content/docs/learn/messaging-patterns/filters.mdx

git commit -m "$(cat <<'EOF'
docs(website): replace filter-vs-middleware prose with comparison table

The "middleware alternative" section in filters.mdx was a one-paragraph
sketch of the choice. Replaces with a scenario-driven comparison table:
each row is a concrete use case, mapped to filter or middleware with a
one-line "why" pointing at the actual contract.

Eight rows covering reading/stamping headers, short-circuiting,
authorising, wrapping with try/finally, timing, catching exceptions,
mutating the body, and unit-of-work patterns. Each row was verified
against the actual IFilter / IMessageProcessingMiddleware /
ISendMessageMiddleware contract before landing.

The closing paragraph keeps the rule of thumb that frames the table.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Mark Group C done in the roadmap

After the three feature commits land, update the roadmap to reflect the completed group.

**Files:**
- Modify: `architecture-fix-plan.md` — change Group C status.

- [ ] **Step 1: Update the roadmap status line**

Open `architecture-fix-plan.md`. Find:

```
## Group C — Docs & contracts · *pending*
```

Change to:

```
## Group C — Docs & contracts · *done — except item 6 (deferred to Group D)*
```

The "deferred to Group D" suffix records that item 6 (supported runtimes / TLS expectations) was deliberately left out of Group C and tied to Group D's TLS-default decision; without that note the next reader will assume the group is incomplete.

- [ ] **Step 2: Commit**

```bash
git add architecture-fix-plan.md

git commit -m "$(cat <<'EOF'
docs(architecture): mark Group C done in the fix plan

Group C lands in three feature commits:

- docs(interfaces): document the at-least-once delivery contract on IBus
- feat(persistence-inmemory): warn on registration; document test/dev scope
- docs(website): replace filter-vs-middleware prose with comparison table

Item 6 (supported runtimes / TLS expectations) is intentionally out
of scope for Group C and will land alongside Group D's TLS-default
decision; the roadmap status line records this so the next reader
doesn't assume the group is incomplete.

Items 4 (factory convention) and 5 (v8 release-notes summary) were
already resolved in Group A. Items 1, 2, 3 of the original Group C
table are now fully implemented.

Group D (security defaults) is the recommended next group per the
roadmap's order, with B + F as the additive low-risk follow-ons.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Final verification

After all four tasks land, run the full sweep to confirm nothing else regressed.

- [ ] **Step 1: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Full unit-test sweep**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore -m:1`
Expected: all tests pass. Count = 1177 (1176 end-of-Group-A baseline + 1 new from Task 2).

- [ ] **Step 3: SerializationCompatTests sweep (sanity)**

Run: `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj --no-build --no-restore`
Expected: 48/48 pass. Group C doesn't touch the serializer; this is a sanity check.

- [ ] **Step 4: Astro site build**

Run: `cd website && npm run build && cd ..`
Expected: build succeeds.

- [ ] **Step 5: Confirm commit chain**

Run: `git log <commit-before-task-1>..HEAD --oneline`
Expected: exactly four commits in this order:

```
<sha> docs(architecture): mark Group C done in the fix plan
<sha> docs(website): replace filter-vs-middleware prose with comparison table
<sha> feat(persistence-inmemory): warn on registration; document test/dev scope
<sha> docs(interfaces): document the at-least-once delivery contract on IBus
```

---

## Risks and rollback

- **`<remarks>` tone** — phrasing chosen above frames the contract neutrally ("delivery is at-least-once") rather than alarmingly. If reviewer feedback during implementation suggests the tone is wrong, adjust within Task 1's commit before final review.
- **Startup warning log noise in tests** — every test that builds a real `ServiceConnectBuilder` with `UseInMemoryPersistence` will emit the warning. The standard escape hatch (raise `ServiceConnect.Persistence.InMemory` to `Error` via log filtering) is documented in the `<remarks>` block. If the test runner becomes noisy, the project's existing test setup can add the filter once globally.
- **Comparison-table accuracy** — Step 1 of Task 3 verifies every row against the actual interface code. If a row turns out to be inaccurate, fix before commit. The risk that survives is one row appearing technically correct but misleading in practice; mitigated by the phrasing being scenario-driven (the "why" column points at the actual constraint, not at marketing text).
- **`InMemoryPersistenceState` factory delegate execution timing** — the warning log fires the first time a service requests the singleton, not at DI-registration time. For the typical bus-build pattern this happens at the first message dispatch. That's the right time semantically (the user has actually committed to using in-memory persistence by then). Documented in Task 2's commit message and `UseInMemoryPersistence` `<remarks>`.
- **Rollback** — each of the four commits is independently revertible. `git revert` of any one commit cleanly removes that item. No data migrations, no API surface changes.

---

## Decisions banked from the brainstorm

For audit trail (cross-referencing the design doc):

- **Three items only** (1, 2, 3 of the original Group C table). Items 4 and 5 are already resolved by Group A; item 6 is deferred to Group D's TLS-default decision.
- **At-least-once contract:** `<remarks>` on `IBus` interface (one canonical block, not duplicated per method); website extension in `idempotency.mdx` (not a new dedicated page).
- **In-memory marker:** XML docs + source-generated `Warning`-level startup log via `Microsoft.Extensions.Logging.Abstractions`. **Not** renamed to `UseInMemoryPersistenceForDevelopment`. **No** environment-aware suppression (standard log filtering is the escape hatch).
- **Filter-vs-middleware:** comparison table replacing the existing one-paragraph "middleware alternative" section in `filters.mdx`. **Not** a new dedicated page; **not** Mermaid; **not** a flowchart.
- **Source-gen logger** chosen over plain `LogWarning(...)` for compile-time message-template validation and stable `EventId`. One file, ~15 lines.
- **Constructor-side vs factory-side log:** factory-side. Factory-side preserves the existing `InMemoryPersistenceState` constructor signature (no ripple through other tests that construct the type directly), and the singleton factory delegate runs exactly once per bus, which is the desired log frequency.
