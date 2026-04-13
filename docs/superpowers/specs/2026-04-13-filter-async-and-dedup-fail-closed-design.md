# Group C-2 — Async Filter Pipeline, Fail-Closed Dedup, and Settings DI

**Date:** 2026-04-13
**Branch:** improvements-and-fixes
**Addresses:** R-017, R-018, R-032
**Builds on:** Group C-1 (CancellationToken + R-034 race) — already merged to branch

## Problem Statement

Three related defects in the deduplication filter subsystem, made tractable together:

1. **R-018 — `IFilter.Process` is synchronous.** The interface returns `bool` and has no `CancellationToken`. Filters that need async I/O (MongoDB deduplication, any persistor) are forced to sync-over-async, e.g. [`MessageDeduplicationPersistorMongoDb.cs:57`](../../../filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorMongoDb.cs) uses `_collection.FindAsync(...).Result`. This blocks pipeline threads and risks deadlock under contention. Group C-1 made the surrounding bus/consumer/producer pipeline async with cancellation; the filter layer is the last sync island.

2. **R-017 — Silent exception swallowing in dedup persistor and outgoing filter.** [`OutgoingFilter.Process`](../../../filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingFilter.cs#L66-L78) catches `Exception`, logs a warning, and returns `true` (continue). The message is sent *without* a dedup record, silently breaking the guarantee the filter exists to provide. The MongoDb persistor's `Insert` and `RemoveExpiredMessages` methods also catch-log-return on any error. Operators discover dedup is broken only from log review — not from behavior.

3. **R-032 — `DeduplicationFilterSettings` is a hand-rolled `Lazy<T>` singleton.** Configuration happens via `DeduplicationFilterSettings.Instance.X = ...` mutation before app startup. This pattern defeats DI, makes testing settings variations awkward, and pre-dates the framework's migration to `Microsoft.Extensions.DependencyInjection`.

## Goals

- Make `IFilter` and `IFilterPipeline` async, threading the CancellationToken established in Group C-1.
- Make `IMessageDeduplicationPersistor` async.
- Make outgoing dedup filter **fail-closed** on persistor exceptions (symmetric with incoming).
- Replace the `DeduplicationFilterSettings` singleton with an `IOptions<T>`-backed DI extension method.
- Retire `PersistorFactory` static class; fold its logic into the extension method.
- Extract the expiry-cleanup loop into an `IHostedService`.

## Non-Goals

- R-009 (service locator anti-pattern in processors) — separate effort.
- R-020/R-021 (ProcessManagerProcessor and Client SRP) — separate effort.
- R-028 (broad unit test coverage) — ongoing.
- Behavioral changes to Gzip filters (signature-only migration).
- Other filter categories (auditing, content routing, etc. if present) — not in scope.

## Design

### 1. IFilter async signature

New interface:

```csharp
public interface IFilter
{
    IBus Bus { get; set; }
    Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default);
}
```

Return value semantics unchanged: `true` **continues** the pipeline, `false` **blocks** (stops) it.

**Documentation bug fix:** [`IFilter.cs`](../../../src/ServiceConnect.Interfaces/IFilter.cs) currently has XML doc comments that state the **inverse** of actual behavior. [`FilterPipeline.ExecuteFilters` line 33-35](../../../src/ServiceConnect/Services/FilterPipeline.cs) treats the return value as `continueProcessing`, and [`IncomingFilter.cs:29`](../../../filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingFilter.cs) returns `false` to block duplicates. Update the XML docs to match reality when renaming to `ProcessAsync`.

**Migration for the six current implementations:**
- `OutgoingDeduplicationFilter` — rewritten (Section 3).
- `IncomingDeduplicationFilter` — rewritten (Section 3).
- `OutgoingFilter` (internal singleton, will be removed — logic moved into `OutgoingDeduplicationFilter`).
- `IncomingFilter` (internal singleton, will be removed — logic moved into `IncomingDeduplicationFilter`).
- `OutgoingGzipCompressionFilter` — body unchanged, returns `Task.FromResult(true)`.
- `IncomingGzipCompressionFilter` — body unchanged, returns `Task.FromResult(true)`.

Gzip filters complete synchronously; wrapping in `Task.FromResult` is appropriate.

### 2. IFilterPipeline async signature

```csharp
public interface IFilterPipeline
{
    Task<bool> ExecuteOutgoingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);
    Task<bool> ExecuteBeforeConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);
    Task<bool> ExecuteAfterConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);
}
```

Implementation in [`FilterPipeline.cs`](../../../src/ServiceConnect/Services/FilterPipeline.cs) — the inner loop becomes:

```csharp
private async Task<bool> ExecuteFiltersAsync(IEnumerable<Type> filterTypes, Envelope envelope, CancellationToken ct)
{
    foreach (var filterType in filterTypes)
    {
        ct.ThrowIfCancellationRequested();
        var filter = (IFilter)serviceProvider.GetRequiredService(filterType);
        var block = await filter.ProcessAsync(envelope, ct).ConfigureAwait(false);
        if (block) return true;
    }
    return false;
}
```

Serial execution preserved. No parallelism (filters may depend on ordering).

### 3. Caller migration

Eight call sites, each already has a CancellationToken available in scope from Group C-1:

| Caller | File:Line | CT source |
|--------|-----------|-----------|
| `Bus.PublishAsync` | Bus.cs:45 | method parameter |
| `Bus.SendAsync` | Bus.cs:63 | method parameter |
| `Bus.SendRequestAsync` | Bus.cs:90 | method parameter |
| `Bus.SendRequestMultiAsync` | Bus.cs:112 | method parameter |
| `Bus.RouteAsync` | Bus.cs:149 | method parameter |
| `MessageDispatcher.Dispatch` | MessageDispatcher.cs:64 | method parameter |
| `MessageDispatcher.Dispatch` | MessageDispatcher.cs:77 | method parameter |
| `MessageDispatcher.Dispatch` | MessageDispatcher.cs:83 | method parameter |

Each becomes `await pipeline.Execute*FiltersAsync(envelope, cancellationToken).ConfigureAwait(false)`.

### 4. IMessageDeduplicationPersistor async

```csharp
public interface IMessageDeduplicationPersistor
{
    Task<bool> GetMessageExistsAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task InsertAsync(Guid messageId, DateTime expiry, CancellationToken cancellationToken = default);
    Task RemoveExpiredMessagesAsync(DateTime expiry, CancellationToken cancellationToken = default);
}
```

**InMemory persistor** — backed by `ConcurrentDictionary`. Sync operations wrapped: `Task.FromResult(exists)`, `Task.CompletedTask` after insert/remove. No exception handling added (dictionary ops don't throw under normal use).

**MongoDb persistor** — driver-native async:
- `GetMessageExistsAsync` → `await _collection.Find(filter).FirstOrDefaultAsync(ct)` (replaces `.FindAsync(...).Result` on line 57).
- `InsertAsync` → `await _collection.InsertOneAsync(item, options: null, cancellationToken: ct)`.
- `RemoveExpiredMessagesAsync` → `await _collection.DeleteManyAsync(filter, ct)`.
- **Remove the inner `try/catch/log.Fatal/return` blocks** (currently lines 61-75 and 77-87). Exceptions must propagate to the filter so the filter can apply the fail-closed policy.

Connection construction: align with Group B's MongoUrl pattern where compatible. The base64 cert loading path (`MongoDbCertBase64`) stays hand-rolled because the MongoDB driver does not natively support loading certificates from memory; this code remains inside the persistor constructor.

### 5. Fail-closed outgoing filter

New [`OutgoingDeduplicationFilter`](../../../filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilter.cs):

```csharp
public sealed class OutgoingDeduplicationFilter : IFilter
{
    private readonly IMessageDeduplicationPersistor _persistor;
    private readonly DeduplicationFilterSettings _settings;

    public IBus Bus { get; set; } = null!;

    public OutgoingDeduplicationFilter(
        IMessageDeduplicationPersistor persistor,
        IOptions<DeduplicationFilterSettings> options)
    {
        _persistor = persistor;
        _settings = options.Value;
    }

    public async Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        var messageId = HeaderDecoder.GetMessageId(envelope.Headers);
        var expiry = DateTime.UtcNow.AddHours(_settings.MsgExpiryHours);
        await _persistor.InsertAsync(messageId, expiry, cancellationToken).ConfigureAwait(false);
        return true; // continue pipeline (true = continue, false = block)
    }
}
```

**Return semantics reminder:** `IFilter.ProcessAsync` returns `true` to **continue** and `false` to **block** (stop the pipeline). Outgoing dedup records the send and continues (`true`). Incoming dedup returns `false` to block duplicates, `true` to continue.

No try/catch. On persistor failure, exception bubbles through `FilterPipeline.ExecuteOutgoingFiltersAsync` → `Bus.PublishAsync` → caller. Caller retries at their own discretion. The dedup guarantee is maintained: no record, no send.

New [`IncomingDeduplicationFilter`](../../../filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilter.cs):

```csharp
public async Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
{
    // Only dedup-check on redelivered messages (see original IncomingFilter comment):
    // RabbitMQ guarantees a first-delivery has never been seen before.
    if (!IsRedelivered(envelope.Headers))
        return true; // continue

    var messageId = HeaderDecoder.GetMessageId(envelope.Headers);
    var exists = await _persistor.GetMessageExistsAsync(messageId, cancellationToken).ConfigureAwait(false);
    if (exists)
        return false; // block duplicate

    return true; // continue
}
```

Persistor exceptions propagate (matches current rethrow behavior at `IncomingFilter.cs:50-54` once the inner swallow is stripped).

The legacy internal `OutgoingFilter`/`IncomingFilter` singleton classes are **deleted** — their logic is now inlined into the public filter types above.

### 6. Settings + DI

`DeduplicationFilterSettings` demoted to POCO:

```csharp
public sealed class DeduplicationFilterSettings
{
    public int MsgExpiryHours { get; set; } = 24;
    public int MsgCleanupIntervalMinutes { get; set; } = 60;
    public string? ConnectionStringMongoDb { get; set; }
    public string? DatabaseNameMongoDb { get; set; }
    public string? CollectionNameMongoDb { get; set; }
    public string? MongoDbCertPath { get; set; }
    public string? MongoDbCertBase64 { get; set; }
    public string? MongoDbCertPassphrase { get; set; }
    public PersistorType PersistorType { get; set; } = PersistorType.InMemory;
    public bool DisableMsgExpiry { get; set; }
}
```

Removed: `Instance`, `Lazy<T>`, any cleanup-scheduling code.

New extension method in `ServiceConnect.Filter.MessageDeduplication`:

```csharp
public static class AddMessageDeduplicationFilterExtensions
{
    public static IServiceCollection AddMessageDeduplicationFilter(
        this IServiceCollection services,
        Action<DeduplicationFilterSettings> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        services.AddSingleton<IMessageDeduplicationPersistor>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<DeduplicationFilterSettings>>().Value;
            return opts.PersistorType switch
            {
                PersistorType.InMemory => new MessageDeduplicationPersistorInMemory(),
                PersistorType.MongoDb  => new MessageDeduplicationPersistorMongoDb(opts),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(opts.PersistorType), opts.PersistorType, "Unsupported persistor type.")
            };
        });

        services.AddTransient<IncomingDeduplicationFilter>();
        services.AddTransient<OutgoingDeduplicationFilter>();
        services.AddHostedService<DeduplicationCleanupHostedService>();

        return services;
    }
}
```

The hosted service is registered unconditionally; it consults `DisableMsgExpiry` at runtime.

`PersistorFactory` static class is **deleted**. Its switch moves into the delegate above.

### 7. Cleanup hosted service

New `DeduplicationCleanupHostedService : BackgroundService`:

```csharp
public sealed class DeduplicationCleanupHostedService(
    IMessageDeduplicationPersistor persistor,
    IOptions<DeduplicationFilterSettings> options,
    ILogger<DeduplicationCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (settings.DisableMsgExpiry) return;

        var interval = TimeSpan.FromMinutes(settings.MsgCleanupIntervalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                var cutoff = DateTime.UtcNow;
                await persistor.RemoveExpiredMessagesAsync(cutoff, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return; // graceful shutdown
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during dedup cleanup; will retry on next interval");
            }
        }
    }
}
```

Non-cancellation exceptions are logged and the loop continues — a transient persistor outage should not silently kill the cleanup process. Distinct from the filter-path fail-closed policy because cleanup runs out-of-band (no caller to propagate to).

### 8. Usage example

```csharp
// Program.cs or startup configuration
services.AddServiceConnect(cfg => { /* ... */ })
    .AddMessageDeduplicationFilter(cfg =>
    {
        cfg.PersistorType = PersistorType.MongoDb;
        cfg.ConnectionStringMongoDb = "mongodb://localhost:27017";
        cfg.DatabaseNameMongoDb = "dedup";
        cfg.CollectionNameMongoDb = "messages";
        cfg.MsgExpiryHours = 24;
        cfg.MsgCleanupIntervalMinutes = 60;
    });

// Pipeline registration (unchanged):
pipeline.BeforeConsuming<IncomingDeduplicationFilter>();
pipeline.Outgoing<OutgoingDeduplicationFilter>();
```

## Testing Strategy

### Unit tests

**`ServiceConnect.UnitTests/FilterPipelineTests.cs`** — convert 7 existing tests to async; Moq setups change from `.Returns(false)` to `.ReturnsAsync(false)`; add one test asserting `ct.ThrowIfCancellationRequested()` fires before the next filter when a pre-cancelled token is passed.

**`MessageDeduplication.UnitTests/OutgoingFilterTests.cs`** — delete `ShouldSwallowPersistanceException`; add:
- `ProcessAsync_PersistorThrows_ExceptionPropagates`
- `ProcessAsync_PreCancelledToken_ThrowsOCE`
- `ProcessAsync_HappyPath_CallsInsertOnce` (verify Moq)

**`MessageDeduplication.UnitTests/IncomingFilterTests.cs`** — async-ify 4 existing tests; add:
- `ProcessAsync_PreCancelledToken_ThrowsOCE`
- `ProcessAsync_PersistorThrows_ExceptionPropagates` (explicit, replaces current "rethrow any internal exception")

**`MessageDeduplication.UnitTests/PersistorFactoryTests.cs`** — **deleted** (class removed).

**`MessageDeduplication.UnitTests/MessageDeduplicationPersistorInMemoryTests.cs` (new):**
- `InsertAsync_ThenGetMessageExistsAsync_ReturnsTrue`
- `GetMessageExistsAsync_UnknownId_ReturnsFalse`
- `RemoveExpiredMessagesAsync_RemovesOnlyExpired`
- Pre-cancelled CT thrown for each method.

**`MessageDeduplication.UnitTests/AddMessageDeduplicationFilterTests.cs` (new):**
- `InMemory_Type_ResolvesInMemoryPersistor`
- `MongoDb_Type_ResolvesMongoDbPersistor` (without connecting — just resolution)
- `ConfiguresOptions_ValuesReachable`
- `RegistersFilters_BothIncomingAndOutgoing`

**`MessageDeduplication.UnitTests/DeduplicationCleanupHostedServiceTests.cs` (new):**
- `DisableMsgExpiryTrue_NeverCallsPersistor`
- `DisableMsgExpiryFalse_CallsRemoveExpiredOnInterval` (short interval, observe 2+ calls)
- `StoppingTokenCancelled_StopsGracefullyWithoutThrow`
- `PersistorThrows_LogsErrorAndContinuesLoop`

### End-to-end tests

[`MessageDeduplicationTests.cs`](../../../src/ServiceConnect.EndToEndTests/MessageDeduplicationTests.cs) — update bus setup to use `AddMessageDeduplicationFilter` extension. No new tests required; existing coverage is sufficient.

### Final validation

1. `dotnet test` — full unit suite green.
2. `sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"` — expect 73/73 passing (no regression from C-1 baseline).

## Breaking Changes

| Change | Who breaks | Migration |
|--------|-----------|-----------|
| `IFilter.Process` → `ProcessAsync` | Anyone with custom `IFilter` impls | Add `async`, rename, return `Task<bool>` |
| `IFilterPipeline` methods renamed + async | Anyone implementing the interface (framework-internal usually) | Rename + async |
| `IMessageDeduplicationPersistor` — all methods async | Anyone with a custom persistor | Migrate signatures to async |
| `DeduplicationFilterSettings.Instance` removed | All users of the dedup filter | Switch to `services.AddMessageDeduplicationFilter(cfg => ...)` |
| `PersistorFactory` static removed | Should be none (was internal helper) | None |
| Outgoing dedup filter fail-closed on persistor error | Users relying on silent dedup-failure tolerance | Wrap `PublishAsync`/`SendAsync` in try/catch if tolerance required |

## Files Changed (estimate)

**Modified (~20):**
- `src/ServiceConnect.Interfaces/IFilter.cs`
- `src/ServiceConnect.Interfaces/IFilterPipeline.cs`
- `src/ServiceConnect/Services/FilterPipeline.cs`
- `src/ServiceConnect/Bus.cs` (5 call sites)
- `src/ServiceConnect/Services/MessageDispatcher.cs` (3 call sites)
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilter.cs`
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilter.cs`
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/IMessageDeduplicationPersistor.cs`
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorInMemory.cs`
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorMongoDb.cs`
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationFilterSettings.cs`
- `filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/OutgoingGzipCompressionFilter.cs`
- `filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/IncomingGzipCompressionFilter.cs`
- `src/ServiceConnect.UnitTests/FilterPipelineTests.cs`
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/OutgoingFilterTests.cs`
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/IncomingFilterTests.cs`
- `src/ServiceConnect.EndToEndTests/MessageDeduplicationTests.cs`
- `docs/remaining-issues.md` (wrap-up task)

**Created (~5):**
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/AddMessageDeduplicationFilterExtensions.cs`
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationCleanupHostedService.cs`
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/MessageDeduplicationPersistorInMemoryTests.cs`
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/AddMessageDeduplicationFilterTests.cs`
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/DeduplicationCleanupHostedServiceTests.cs`

**Deleted (~4):**
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/PersistorFactory.cs`
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingFilter.cs` (logic moved into `OutgoingDeduplicationFilter`)
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingFilter.cs` (logic moved into `IncomingDeduplicationFilter`)
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/PersistorFactoryTests.cs`

Implementer: verify the exact filter project paths under `filters/` match; directory structure may differ slightly from the paths shown above.
