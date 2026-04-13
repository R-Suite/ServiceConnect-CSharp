# Group C-2: Async Filter Pipeline + Fail-Closed Dedup + Settings DI — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Migrate `IFilter`/`IFilterPipeline` to async with CancellationToken; make dedup persistor async; make outgoing dedup fail-closed; replace `DeduplicationFilterSettings` singleton with `IOptions<T>`-backed DI.

**Architecture:** Six sequential commits. Each leaves the build green and all tests passing. We break the interface first (Task 1, atomic across the whole surface), migrate persistor next (Task 2), collapse the legacy internal helpers into the public filter classes with the new fail-closed policy (Task 3), migrate settings/registration (Task 4), add the cleanup hosted service (Task 5), then wrap-up.

**Tech Stack:** C# 12 / .NET 10, xUnit, Moq, MongoDB.Driver 2.23.1 (bump from 2.4.4), Microsoft.Extensions.Options, Microsoft.Extensions.Hosting.Abstractions.

**Spec:** [docs/superpowers/specs/2026-04-13-filter-async-and-dedup-fail-closed-design.md](../specs/2026-04-13-filter-async-and-dedup-fail-closed-design.md)

**Key prior-art references:**
- Group C-1 precedent for CT threading: commits on `improvements-and-fixes` branch, particularly [src/ServiceConnect.Interfaces/IBus.cs](../../../src/ServiceConnect.Interfaces/IBus.cs) and [src/ServiceConnect/Bus.cs](../../../src/ServiceConnect/Bus.cs).
- Group B precedent for MongoDB Driver 2.23.1 + MongoUrl-style construction: [src/ServiceConnect.Persistence.MongoDb/MongoClientFactory.cs](../../../src/ServiceConnect.Persistence.MongoDb/MongoClientFactory.cs).
- Header decoding helper: [src/ServiceConnect.Interfaces/HeaderDecoder.cs](../../../src/ServiceConnect.Interfaces/HeaderDecoder.cs) — `HeaderDecoder.Decode(envelope.Headers["MessageId"])` is the conventional pattern.

**Return value semantics (IMPORTANT — the current XML doc on IFilter.cs is backwards):**
- `IFilter.ProcessAsync` returns `true` to **continue** the pipeline, `false` to **block** (stop).
- `IFilterPipeline.Execute*FiltersAsync` returns `true` if the pipeline was **blocked** by some filter, `false` if all filters passed and execution continued.

These semantics match the existing [FilterPipeline.cs:33-35](../../../src/ServiceConnect/Services/FilterPipeline.cs) implementation. We are **fixing the doc** to match reality, not changing behavior.

---

## File Inventory

**Interfaces (Core):**
- `src/ServiceConnect.Interfaces/IFilter.cs` — modify (rename method, fix XML doc, add CT)
- `src/ServiceConnect.Interfaces/IFilterPipeline.cs` — modify (rename methods, add CT)

**Core implementations:**
- `src/ServiceConnect/Services/FilterPipeline.cs` — modify (async impl)
- `src/ServiceConnect/Bus.cs` — modify (5 call sites)
- `src/ServiceConnect/Services/MessageDispatcher.cs` — modify (3 call sites)

**Filter project — Message Deduplication:**
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.csproj` — modify (add packages, bump mongo driver)
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationFilterSettings.cs` — rewrite (demote from singleton to POCO)
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/PersistorFactory.cs` — delete
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/AddMessageDeduplicationFilterExtensions.cs` — create
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationCleanupHostedService.cs` — create
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilter.cs` — rewrite (inline old logic, fail-closed, DI)
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilter.cs` — rewrite (inline old logic, DI)
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingFilter.cs` — delete
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingFilter.cs` — delete
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/IMessageDeduplicationPersistor.cs` — rewrite (async interface)
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorInMemory.cs` — rewrite (async impl)
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorMongoDb.cs` — rewrite (async impl, remove inner swallows)

**Filter project — Gzip Compression:**
- `filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/OutgoingGzipCompressionFilter.cs` — modify (signature only)
- `filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/IncomingGzipCompressionFilter.cs` — modify (signature only)

**Unit Tests:**
- `src/ServiceConnect.UnitTests/FilterPipelineTests.cs` — rewrite (async)
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/OutgoingFilterTests.cs` — rewrite (targets new wrapper filter, fail-closed)
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/IncomingFilterTests.cs` — rewrite (targets new wrapper filter, async)
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/PersistorFactoryTests.cs` — delete
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/MessageDeduplicationPersistorInMemoryTests.cs` — create
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/AddMessageDeduplicationFilterTests.cs` — create
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/DeduplicationCleanupHostedServiceTests.cs` — create
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/ServiceConnect.Filters.MessageDeduplication.Tests.csproj` — modify (add Microsoft.Extensions.DependencyInjection for registration tests)

**E2E Tests:**
- `src/ServiceConnect.EndToEndTests/MessageDeduplicationTests.cs` — modify (`TestDeduplicationFilter` becomes async)

**Docs:**
- `docs/remaining-issues.md` — modify (mark R-017/R-018/R-032 as done)

---

## Task 1: Async IFilter + IFilterPipeline + Callers + Stub Implementations

**Rationale:** The whole interface change lands atomically — interface, pipeline impl, all 8 callers, and every existing `IFilter` implementation in the repo. Existing impls are stubbed by wrapping their sync bodies in `Task.FromResult`. `FilterPipelineTests` rewritten. Build green + all tests green at end.

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IFilter.cs`
- Modify: `src/ServiceConnect.Interfaces/IFilterPipeline.cs`
- Modify: `src/ServiceConnect/Services/FilterPipeline.cs`
- Modify: `src/ServiceConnect/Bus.cs` (5 sites: lines 45, 63, 90, 112, 149)
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs` (3 sites: lines 64, 77, 83)
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilter.cs`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilter.cs`
- Modify: `filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/OutgoingGzipCompressionFilter.cs`
- Modify: `filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/IncomingGzipCompressionFilter.cs`
- Modify: `src/ServiceConnect.EndToEndTests/MessageDeduplicationTests.cs` (TestDeduplicationFilter inner class)
- Rewrite: `src/ServiceConnect.UnitTests/FilterPipelineTests.cs`

- [ ] **Step 1: Rewrite `IFilter.cs` with async signature and corrected XML doc**

Replace the entire body of `src/ServiceConnect.Interfaces/IFilter.cs` with:

```csharp
namespace ServiceConnect.Interfaces;

/// <summary>
/// A filter that inspects or modifies messages as they pass through the pipeline.
/// </summary>
public interface IFilter
{
    /// <summary>
    /// The bus instance available for use by the filter.
    /// </summary>
    IBus Bus { get; set; }

    /// <summary>
    /// Processes the given envelope. Returns <c>true</c> to <b>continue</b> pipeline execution;
    /// returns <c>false</c> to <b>block</b> the message and stop further pipeline execution.
    /// </summary>
    Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default);
}
```

Note: `true = continue, false = block` — this matches actual `FilterPipeline.cs:33-35` behavior. The old doc had it inverted.

- [ ] **Step 2: Rewrite `IFilterPipeline.cs` with async signatures**

Replace the entire body of `src/ServiceConnect.Interfaces/IFilterPipeline.cs` with:

```csharp
namespace ServiceConnect.Interfaces;

/// <summary>
/// Manages the execution of outgoing and consuming filter stages.
/// </summary>
public interface IFilterPipeline
{
    /// <summary>
    /// Executes all outgoing filters. Returns <c>true</c> if any filter blocked the message.
    /// </summary>
    Task<bool> ExecuteOutgoingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes all before-consuming filters. Returns <c>true</c> if any filter blocked the message.
    /// </summary>
    Task<bool> ExecuteBeforeConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes all after-consuming filters. Returns <c>true</c> if any filter blocked the message.
    /// </summary>
    Task<bool> ExecuteAfterConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: Rewrite `FilterPipeline.cs` with async implementation**

Replace the entire body of `src/ServiceConnect/Services/FilterPipeline.cs` with:

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

public sealed class FilterPipeline(IPipelineConfiguration config, IServiceProvider serviceProvider) : IFilterPipeline
{
    public Task<bool> ExecuteOutgoingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        return ExecuteFiltersAsync(config.OutgoingFilters, envelope, cancellationToken);
    }

    public Task<bool> ExecuteBeforeConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        return ExecuteFiltersAsync(config.BeforeConsumingFilters, envelope, cancellationToken);
    }

    public Task<bool> ExecuteAfterConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        return ExecuteFiltersAsync(config.AfterConsumingFilters, envelope, cancellationToken);
    }

    private async Task<bool> ExecuteFiltersAsync(IList<Type> filterTypes, Envelope envelope, CancellationToken cancellationToken)
    {
        if (filterTypes == null || filterTypes.Count == 0)
            return false;

        foreach (Type filterType in filterTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filter = (IFilter)serviceProvider.GetRequiredService(filterType);

            bool continueProcessing = await filter.ProcessAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (!continueProcessing)
                return true; // stopped
        }

        return false; // not stopped
    }
}
```

- [ ] **Step 4: Update the 5 `Bus.cs` call sites**

For each of the 5 sites, change the sync call to async + forward the local `cancellationToken`. The sites (with surrounding context shown for uniqueness):

Site 1 — `PublishAsync`, around line 45:

```csharp
        if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
            return;
```

Site 2 — `SendAsync`, around line 63 (identical pattern):

```csharp
        if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
            return;
```

Site 3 — `SendRequestAsync`, around line 90:

```csharp
        if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Outgoing filters blocked the request message.");
```

Site 4 — `SendRequestMultiAsync`, around line 112:

```csharp
        if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Outgoing filters blocked the request message.");
```

Site 5 — `RouteAsync`, around line 149:

```csharp
        if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
            return;
```

- [ ] **Step 5: Update the 3 `MessageDispatcher.cs` call sites**

In `src/ServiceConnect/Services/MessageDispatcher.cs`:

Site 1 — replace line 64's `bool blocked = _filterPipeline.ExecuteBeforeConsumingFilters(envelope);` with:

```csharp
            bool blocked = await _filterPipeline.ExecuteBeforeConsumingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
```

Site 2 — inside the `RunProcessors` local function (around line 77), replace `_filterPipeline.ExecuteAfterConsumingFilters(e);` with:

```csharp
                        await _filterPipeline.ExecuteAfterConsumingFiltersAsync(e, ct).ConfigureAwait(false);
```

(note: uses `ct` — the `RunProcessors` local function's CancellationToken parameter — not `cancellationToken`)

Site 3 — the fall-through at around line 83, replace `_filterPipeline.ExecuteAfterConsumingFilters(e);` with:

```csharp
                _logger.LogWarning("No processor handled message of type {MessageType}", mt.FullName);
                await _filterPipeline.ExecuteAfterConsumingFiltersAsync(e, ct).ConfigureAwait(false);
                return new ConsumeEventResult { Success = true };
```

The enclosing `RunProcessors` function is already `async Task<ConsumeEventResult>` so `await` is legal.

- [ ] **Step 6: Stub the dedup wrapper filter signatures async**

Replace the body of `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilter.cs` with:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class OutgoingDeduplicationFilter : IFilter
    {
        private static readonly Lazy<OutgoingFilter> _outgoingFilter = new(() =>
            new OutgoingFilter(PersistorFactory.Create(DeduplicationFilterSettings.Instance.PersistorType)));

        public IBus Bus { get; set; }

        public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_outgoingFilter.Value.Process(envelope));
        }
    }
}
```

Similarly replace `IncomingDeduplicationFilter.cs` with:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class IncomingDeduplicationFilter : IFilter
    {
        private static readonly Lazy<IncomingFilter> _incomingFilter = new(() =>
            new IncomingFilter(PersistorFactory.Create(DeduplicationFilterSettings.Instance.PersistorType)));

        public IBus Bus { get; set; }

        public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_incomingFilter.Value.Process(envelope));
        }
    }
}
```

The internal `OutgoingFilter` / `IncomingFilter` helper classes (same directory) remain untouched for now — their `Process` methods are still sync and match the still-sync `IMessageDeduplicationPersistor`. Task 2 migrates those.

- [ ] **Step 7: Stub the Gzip filter signatures async**

Read `filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/OutgoingGzipCompressionFilter.cs` to see the current body. Rename `public bool Process(Envelope envelope)` to `public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)`. Change every `return true;` / `return false;` at the bottom of the method to `return Task.FromResult(true);` / `return Task.FromResult(false);`. Preserve all other logic (compression, bounds-checking).

Apply the identical transform to `IncomingGzipCompressionFilter.cs` in the same directory.

- [ ] **Step 8: Update `TestDeduplicationFilter` in the E2E test**

In `src/ServiceConnect.EndToEndTests/MessageDeduplicationTests.cs`, change the file-scoped `TestDeduplicationFilter.Process` method to async. The current body returns `bool` values at multiple points. Rewrite the method signature and wrap each return:

```csharp
    public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        if (!envelope.Headers.TryGetValue(HeaderKeys.MessageId, out var rawId))
            return Task.FromResult(true); // no MessageId header — let it through

        var messageId = rawId is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : rawId?.ToString() ?? string.Empty;

        if (string.IsNullOrEmpty(messageId))
            return Task.FromResult(true);

        if (_seen.TryAdd(messageId, 0))
            return Task.FromResult(true); // first time seeing this ID — allow processing

        // Already seen — block if this is a redelivery
        if (!envelope.Headers.TryGetValue(HeaderKeys.Redelivered, out var rawRedelivered))
            return Task.FromResult(true);

        var redeliveredStr = rawRedelivered is byte[] redeliveredBytes
            ? Encoding.UTF8.GetString(redeliveredBytes)
            : rawRedelivered?.ToString() ?? string.Empty;

        return Task.FromResult(!string.Equals(redeliveredStr, "True", StringComparison.OrdinalIgnoreCase));
    }
```

- [ ] **Step 9: Rewrite `FilterPipelineTests.cs` for async**

Replace the entire content of `src/ServiceConnect.UnitTests/FilterPipelineTests.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests
{
    // Marker abstract classes for ordering tests
    public abstract class FakeFilter1 : IFilter
    {
        public IBus Bus { get; set; } = null!;
        public abstract Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default);
    }

    public abstract class FakeFilter2 : IFilter
    {
        public IBus Bus { get; set; } = null!;
        public abstract Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default);
    }

    public class FilterPipelineTests
    {
        private readonly PipelineConfiguration _config;
        private readonly Mock<IServiceProvider> _mockServiceProvider;
        private readonly FilterPipeline _pipeline;

        public FilterPipelineTests()
        {
            _config = new PipelineConfiguration();
            _mockServiceProvider = new Mock<IServiceProvider>();
            _pipeline = new FilterPipeline(_config, _mockServiceProvider.Object);
        }

        [Fact]
        public async Task ExecuteOutgoingFiltersAsync_WithNoFilters_ReturnsFalse()
        {
            var envelope = new Envelope();
            var result = await _pipeline.ExecuteOutgoingFiltersAsync(envelope);
            Assert.False(result);
        }

        [Fact]
        public async Task ExecuteOutgoingFiltersAsync_WhenFilterReturnsTrue_ReturnsFalse()
        {
            // true from filter = continue processing => pipeline returns false (not stopped)
            var mockFilter = new Mock<FakeFilter1>();
            mockFilter.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(mockFilter.Object);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));

            var envelope = new Envelope();
            var result = await _pipeline.ExecuteOutgoingFiltersAsync(envelope);

            Assert.False(result);
        }

        [Fact]
        public async Task ExecuteOutgoingFiltersAsync_WhenFilterReturnsFalse_ReturnsTrue()
        {
            // false from filter = stop pipeline => pipeline returns true (stopped)
            var mockFilter = new Mock<FakeFilter1>();
            mockFilter.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(mockFilter.Object);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));

            var envelope = new Envelope();
            var result = await _pipeline.ExecuteOutgoingFiltersAsync(envelope);

            Assert.True(result);
        }

        [Fact]
        public async Task ExecuteOutgoingFiltersAsync_ExecutesFiltersInOrder()
        {
            var callOrder = new List<string>();

            var mockFilter1 = new Mock<FakeFilter1>();
            mockFilter1.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
                .Callback(() => callOrder.Add("filter1"))
                .ReturnsAsync(true);

            var mockFilter2 = new Mock<FakeFilter2>();
            mockFilter2.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
                .Callback(() => callOrder.Add("filter2"))
                .ReturnsAsync(true);

            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(mockFilter1.Object);
            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter2)))
                .Returns(mockFilter2.Object);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));
            _config.OutgoingFilters.Add(typeof(FakeFilter2));

            var envelope = new Envelope();
            await _pipeline.ExecuteOutgoingFiltersAsync(envelope);

            Assert.Equal(new[] { "filter1", "filter2" }, callOrder);
        }

        [Fact]
        public async Task ExecuteOutgoingFiltersAsync_StopsAtFirstBlockingFilter()
        {
            var mockFilter1 = new Mock<FakeFilter1>();
            mockFilter1.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(false); // blocks

            var mockFilter2 = new Mock<FakeFilter2>();
            mockFilter2.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(mockFilter1.Object);
            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter2)))
                .Returns(mockFilter2.Object);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));
            _config.OutgoingFilters.Add(typeof(FakeFilter2));

            var envelope = new Envelope();
            await _pipeline.ExecuteOutgoingFiltersAsync(envelope);

            // Filter2 should never have been called
            mockFilter2.Verify(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ExecuteOutgoingFiltersAsync_ThrowsWhenFilterNotRegistered()
        {
            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(null!);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));

            var envelope = new Envelope();
            await Assert.ThrowsAsync<InvalidOperationException>(() => _pipeline.ExecuteOutgoingFiltersAsync(envelope));
        }

        [Fact]
        public async Task ExecuteBeforeConsumingFiltersAsync_WithNoFilters_ReturnsFalse()
        {
            var envelope = new Envelope();
            var result = await _pipeline.ExecuteBeforeConsumingFiltersAsync(envelope);
            Assert.False(result);
        }

        [Fact]
        public async Task ExecuteAfterConsumingFiltersAsync_WithNoFilters_ReturnsFalse()
        {
            var envelope = new Envelope();
            var result = await _pipeline.ExecuteAfterConsumingFiltersAsync(envelope);
            Assert.False(result);
        }

        [Fact]
        public async Task ExecuteOutgoingFiltersAsync_PreCancelledToken_ThrowsOCEBeforeFilterRuns()
        {
            var mockFilter = new Mock<FakeFilter1>();
            mockFilter.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(mockFilter.Object);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var envelope = new Envelope();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                _pipeline.ExecuteOutgoingFiltersAsync(envelope, cts.Token));

            mockFilter.Verify(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
```

- [ ] **Step 9b: Verify the stub — make sure internal `OutgoingFilter`/`IncomingFilter` helpers still compile**

Read `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingFilter.cs` and confirm it does **not** implement `IFilter` (it has its own `bool Process(Envelope envelope)` method but no `: IFilter`). Same for `IncomingFilter.cs`. If either now fails to compile, it implies they implement `IFilter` and must also be stubbed — in that case add `ProcessAsync` alongside the existing `Process` and migrate in Task 2.

- [ ] **Step 10: Run the build**

```bash
dotnet build
```

Expected: build succeeds with 0 errors. Warnings are acceptable at this stage but should not have increased.

- [ ] **Step 11: Run the unit tests**

```bash
dotnet test --filter "Category!=Docker"
```

Expected: all tests pass. `FilterPipelineTests` now has 9 tests (was 7 — added the pre-cancelled test, and the no-filters tests for before/after were already there). Existing `OutgoingFilterTests` and `IncomingFilterTests` still pass because they target the internal sync helpers which we did not touch.

- [ ] **Step 12: Commit**

```bash
git add src/ServiceConnect.Interfaces/IFilter.cs src/ServiceConnect.Interfaces/IFilterPipeline.cs \
    src/ServiceConnect/Services/FilterPipeline.cs \
    src/ServiceConnect/Bus.cs src/ServiceConnect/Services/MessageDispatcher.cs \
    filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilter.cs \
    filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilter.cs \
    filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/OutgoingGzipCompressionFilter.cs \
    filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/IncomingGzipCompressionFilter.cs \
    src/ServiceConnect.EndToEndTests/MessageDeduplicationTests.cs \
    src/ServiceConnect.UnitTests/FilterPipelineTests.cs
git commit -m "feat(filter): migrate IFilter and IFilterPipeline to async (Group C-2)

IFilter.Process -> IFilter.ProcessAsync with CancellationToken.
IFilterPipeline methods likewise renamed to async.
Bus (5 call sites) and MessageDispatcher (3 call sites) thread CT.
Gzip filters and dedup wrapper filters stubbed via Task.FromResult.
FilterPipelineTests rewritten for async including pre-cancelled test.

Fixes inverted XML doc on IFilter: returns true=continue, false=block
(matches actual FilterPipeline.ExecuteFilters impl).

Addresses part of R-018."
```

---

## Task 2: IMessageDeduplicationPersistor Async (InMemory + MongoDb)

**Rationale:** Make the persistor interface async, migrate the two impls. MongoDb persistor bumps driver to 2.23.1 (aligns with Group B), removes inner `try/catch/log.Fatal/return` swallows, switches to driver-native `FirstOrDefaultAsync`/`InsertOneAsync`/`DeleteManyAsync`. InMemory wraps sync ops. The legacy `OutgoingFilter` / `IncomingFilter` helper classes in `Filters/` also need updating because they call the persistor; they become sync-calls-async via `GetAwaiter().GetResult()` **temporarily** — Task 3 deletes them. This ugly bridge exists for one task only.

**Files:**
- Rewrite: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/IMessageDeduplicationPersistor.cs`
- Rewrite: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorInMemory.cs`
- Rewrite: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorMongoDb.cs`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingFilter.cs` (temporarily bridge to async)
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingFilter.cs` (temporarily bridge to async)
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.csproj` (bump mongo driver)
- Create: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/MessageDeduplicationPersistorInMemoryTests.cs`

- [ ] **Step 1: Write the failing InMemory persistor tests first**

Create `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/MessageDeduplicationPersistorInMemoryTests.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using Xunit;

namespace ServiceConnect.Filters.MessageDeduplication.Tests
{
    public class MessageDeduplicationPersistorInMemoryTests
    {
        [Fact]
        public async Task InsertAsync_ThenGetMessageExistsAsync_ReturnsTrue()
        {
            var persistor = new MessageDeduplicationPersistorInMemory();
            var id = Guid.NewGuid();

            await persistor.InsertAsync(id, DateTime.UtcNow.AddHours(1));
            var exists = await persistor.GetMessageExistsAsync(id);

            Assert.True(exists);
        }

        [Fact]
        public async Task GetMessageExistsAsync_UnknownId_ReturnsFalse()
        {
            var persistor = new MessageDeduplicationPersistorInMemory();
            var exists = await persistor.GetMessageExistsAsync(Guid.NewGuid());
            Assert.False(exists);
        }

        [Fact]
        public async Task RemoveExpiredMessagesAsync_RemovesOnlyExpired()
        {
            var persistor = new MessageDeduplicationPersistorInMemory();
            var expired = Guid.NewGuid();
            var fresh = Guid.NewGuid();

            await persistor.InsertAsync(expired, DateTime.UtcNow.AddHours(-1)); // already expired
            await persistor.InsertAsync(fresh, DateTime.UtcNow.AddHours(1));

            await persistor.RemoveExpiredMessagesAsync(DateTime.UtcNow);

            Assert.False(await persistor.GetMessageExistsAsync(expired));
            Assert.True(await persistor.GetMessageExistsAsync(fresh));
        }

        [Fact]
        public async Task InsertAsync_PreCancelledToken_ThrowsOCE()
        {
            var persistor = new MessageDeduplicationPersistorInMemory();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                persistor.InsertAsync(Guid.NewGuid(), DateTime.UtcNow.AddHours(1), cts.Token));
        }

        [Fact]
        public async Task GetMessageExistsAsync_PreCancelledToken_ThrowsOCE()
        {
            var persistor = new MessageDeduplicationPersistorInMemory();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                persistor.GetMessageExistsAsync(Guid.NewGuid(), cts.Token));
        }

        [Fact]
        public async Task RemoveExpiredMessagesAsync_PreCancelledToken_ThrowsOCE()
        {
            var persistor = new MessageDeduplicationPersistorInMemory();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                persistor.RemoveExpiredMessagesAsync(DateTime.UtcNow, cts.Token));
        }
    }
}
```

- [ ] **Step 2: Run the new tests — they should fail to compile**

```bash
dotnet test filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/ServiceConnect.Filters.MessageDeduplication.Tests.csproj
```

Expected: build fails — `InsertAsync`, `GetMessageExistsAsync`, `RemoveExpiredMessagesAsync` don't exist on the interface yet.

- [ ] **Step 3: Rewrite `IMessageDeduplicationPersistor.cs` with async methods**

Replace the entire body of `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/IMessageDeduplicationPersistor.cs` with:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors
{
    public interface IMessageDeduplicationPersistor
    {
        /// <summary>
        /// Returns true if the message id exists in the relevant persistant storage.
        ///  => the message has been previously processed.
        /// </summary>
        Task<bool> GetMessageExistsAsync(Guid messageId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Inserts a processed message into the relevant persistant storage.
        /// This happens immediately after the message has been processed.
        /// </summary>
        Task InsertAsync(Guid messageId, DateTime messageExpiry, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes all the expired message ids from the relevant persistant storage.
        /// This prevents the storage size from growing indefinitely.
        /// </summary>
        Task RemoveExpiredMessagesAsync(DateTime messageExpiry, CancellationToken cancellationToken = default);
    }
}
```

- [ ] **Step 4: Rewrite `MessageDeduplicationPersistorInMemory.cs` to match**

Replace the entire body with:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors
{
    /// <summary>
    /// InMemory implementation of the persistor. Keeps processed message ids in a concurrent dictionary.
    /// </summary>
    public class MessageDeduplicationPersistorInMemory : IMessageDeduplicationPersistor
    {
        private static readonly ConcurrentDictionary<string, CacheItem> Cache = new ConcurrentDictionary<string, CacheItem>();

        public Task<bool> GetMessageExistsAsync(Guid messageId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Cache.ContainsKey(messageId.ToString()));
        }

        public Task InsertAsync(Guid messageId, DateTime messageExpiry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Cache.TryAdd(messageId.ToString(), new CacheItem { MessageExpiry = messageExpiry });
            return Task.CompletedTask;
        }

        public Task RemoveExpiredMessagesAsync(DateTime messageExpiry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (KeyValuePair<string, CacheItem> cacheItem in Cache)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cacheItem.Value.MessageExpiry < messageExpiry)
                {
                    Cache.TryRemove(cacheItem.Key, out _);
                }
            }
            return Task.CompletedTask;
        }
    }

    internal sealed class CacheItem
    {
        public object Value { get; set; }
        public DateTime MessageExpiry { get; set; }
    }
}
```

- [ ] **Step 5: Bump MongoDB driver and rewrite `MessageDeduplicationPersistorMongoDb.cs`**

First, edit `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.csproj`. Change:

```xml
    <PackageReference Include="mongocsharpdriver" Version="2.4.4" />
```

to:

```xml
    <PackageReference Include="MongoDB.Driver" Version="2.23.1" />
```

Then replace the entire body of `MessageDeduplicationPersistorMongoDb.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors
{
    public class MessageDeduplicationPersistorMongoDb : IMessageDeduplicationPersistor
    {
        private readonly IMongoCollection<ProcessedMessage> _collection;

        public MessageDeduplicationPersistorMongoDb()
        {
            var filterSettings = DeduplicationFilterSettings.Instance;

            var url = new MongoUrl(filterSettings.ConnectionStringMongoDb);
            var clientSettings = MongoClientSettings.FromUrl(url);

            if (!string.IsNullOrEmpty(filterSettings.MongoDbCertPath) ||
                !string.IsNullOrEmpty(filterSettings.MongoDbCertBase64))
            {
                X509Certificate2 cert;
                if (!string.IsNullOrEmpty(filterSettings.MongoDbCertPath))
                {
                    cert = string.IsNullOrEmpty(filterSettings.MongoDbCertPassphrase)
                        ? new X509Certificate2(filterSettings.MongoDbCertPath)
                        : new X509Certificate2(filterSettings.MongoDbCertPath, filterSettings.MongoDbCertPassphrase);
                }
                else
                {
                    var certBytes = Convert.FromBase64String(filterSettings.MongoDbCertBase64);
                    cert = string.IsNullOrEmpty(filterSettings.MongoDbCertPassphrase)
                        ? new X509Certificate2(certBytes)
                        : new X509Certificate2(certBytes, filterSettings.MongoDbCertPassphrase);
                }

                clientSettings.UseTls = true;
                clientSettings.SslSettings = new SslSettings
                {
                    ClientCertificates = new List<X509Certificate> { cert },
                    ClientCertificateSelectionCallback = (sender, host, certificates, certificate, issuers) => certificates[0],
                    CheckCertificateRevocation = true
                };
            }

            var mongoClient = new MongoClient(clientSettings);
            var mongoDatabase = mongoClient.GetDatabase(filterSettings.DatabaseNameMongoDb);
            _collection = mongoDatabase.GetCollection<ProcessedMessage>(filterSettings.CollectionNameMongoDb);

            // Ensure indexes (fire-and-forget during construction is existing behavior).
            _collection.Indexes.CreateOneAsync(
                new CreateIndexModel<ProcessedMessage>(Builders<ProcessedMessage>.IndexKeys.Ascending(_ => _.Id)));
            _collection.Indexes.CreateOneAsync(
                new CreateIndexModel<ProcessedMessage>(Builders<ProcessedMessage>.IndexKeys.Ascending(_ => _.ExpiryDateTime)));
        }

        public async Task<bool> GetMessageExistsAsync(Guid messageId, CancellationToken cancellationToken = default)
        {
            var found = await _collection.Find(i => i.Id == messageId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return found != null;
        }

        public Task InsertAsync(Guid messageId, DateTime messageExpiry, CancellationToken cancellationToken = default)
        {
            return _collection.InsertOneAsync(
                new ProcessedMessage
                {
                    Id = messageId,
                    ExpiryDateTime = messageExpiry
                },
                options: null,
                cancellationToken: cancellationToken);
        }

        public Task RemoveExpiredMessagesAsync(DateTime messageExpiry, CancellationToken cancellationToken = default)
        {
            return _collection.DeleteManyAsync(i => i.ExpiryDateTime < messageExpiry, cancellationToken);
        }
    }
}
```

Note: `UseSsl` was renamed to `UseTls` in modern driver versions; we flip to `UseTls = true`. The `Common.Logging` using and the `Logger` field are **removed** — there is no more internal swallow to log.

The `InsertOne(...)` call's switch to `InsertOneAsync(...)` removes the surrounding try/catch. The `DeleteMany(...)` call's switch to `DeleteManyAsync(...)` likewise removes the try/catch. Exceptions now propagate to the filter, which will propagate them to the caller (fail-closed policy in Task 3).

- [ ] **Step 6: Temporarily bridge legacy `OutgoingFilter.cs` to async persistor**

The internal helper `OutgoingFilter.cs` at `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingFilter.cs` calls `_messageDeduplicationPersistor.Insert(...)` and `_messageDeduplicationPersistor.RemoveExpiredMessages(...)` on lines 56 and 68. These names no longer exist.

Change line 56 (inside `Callback`) from:

```csharp
                _messageDeduplicationPersistor.RemoveExpiredMessages(DateTime.UtcNow);
```

to:

```csharp
                _messageDeduplicationPersistor.RemoveExpiredMessagesAsync(DateTime.UtcNow).GetAwaiter().GetResult();
```

Change line 68 (inside `Process`) from:

```csharp
                _messageDeduplicationPersistor.Insert(
                    new Guid(HeaderDecoder.Decode(envelope.Headers["MessageId"]) ?? string.Empty),
                    DateTime.UtcNow.AddHours(_settings.MsgExpiryHours));
```

to:

```csharp
                _messageDeduplicationPersistor.InsertAsync(
                    new Guid(HeaderDecoder.Decode(envelope.Headers["MessageId"]) ?? string.Empty),
                    DateTime.UtcNow.AddHours(_settings.MsgExpiryHours))
                    .GetAwaiter().GetResult();
```

This is a **temporary bridge** — Task 3 deletes this file. We do this to keep the build green within this commit's scope.

- [ ] **Step 7: Temporarily bridge legacy `IncomingFilter.cs` to async persistor**

`IncomingFilter.cs:42-43` calls `_messageDeduplicationPersistor.GetMessageExists(...)`. Change it to:

```csharp
                    bool msgAlreadyProcessed =
                        _messageDeduplicationPersistor.GetMessageExistsAsync(
                            new Guid(HeaderDecoder.Decode(envelope.Headers["MessageId"]) ?? string.Empty))
                            .GetAwaiter().GetResult();
```

Also a temporary bridge — deleted in Task 3.

- [ ] **Step 8: Build and confirm the new InMemory tests now pass**

```bash
dotnet build
dotnet test filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/ServiceConnect.Filters.MessageDeduplication.Tests.csproj
```

Expected: build succeeds. `MessageDeduplicationPersistorInMemoryTests` 6 tests pass. Existing `OutgoingFilterTests` and `IncomingFilterTests` still pass (they mock `IMessageDeduplicationPersistor` — Moq's `Mock<IMessageDeduplicationPersistor>` will auto-generate async methods that return `Task.FromResult(default)`, but the existing setup uses `.Setup(i => i.Insert(...))` which no longer exists).

**Important:** the existing tests will fail to compile at Step 2. That's expected — but they should now compile after the interface migration *if* you leave them targeting the sync-via-`GetAwaiter` bridge… except they mock `IMessageDeduplicationPersistor.Insert` which is gone. We must update the tests here too.

In `OutgoingFilterTests.cs` change:

```csharp
            _persistor.Setup(i => i.Insert(messageId, It.IsAny<DateTime>()));
```

to:

```csharp
            _persistor.Setup(i => i.InsertAsync(messageId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
```

And in the `ShouldSwallowPersistanceException` test, change:

```csharp
            _persistor.Setup(i => i.Insert(messageId, It.IsAny<DateTime>())).Throws(new Exception());
```

to:

```csharp
            _persistor.Setup(i => i.InsertAsync(messageId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception());
```

Add `using System.Threading;` and `using System.Threading.Tasks;` at the top if not already present.

In `IncomingFilterTests.cs` change:

```csharp
            _persistor.Setup(i => i.GetMessageExists(messageId)).Returns(true);
```

to:

```csharp
            _persistor.Setup(i => i.GetMessageExistsAsync(messageId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
```

And:

```csharp
            _persistor.Setup(i => i.GetMessageExists(messageId)).Throws(new Exception());
```

to:

```csharp
            _persistor.Setup(i => i.GetMessageExistsAsync(messageId, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception());
```

Add `using System.Threading;` at the top.

`PersistorFactoryTests.cs` is unchanged — it calls `PersistorFactory.Create` which still exists.

- [ ] **Step 9: Run the full unit test suite**

```bash
dotnet test --filter "Category!=Docker"
```

Expected: all tests pass. Old `OutgoingFilterTests` still validate the legacy helper's swallow behavior (unchanged — we modify that in Task 3), just with new Moq setup syntax.

- [ ] **Step 10: Commit**

```bash
git add filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/ \
    filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingFilter.cs \
    filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingFilter.cs \
    filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.csproj \
    filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/MessageDeduplicationPersistorInMemoryTests.cs \
    filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/OutgoingFilterTests.cs \
    filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/IncomingFilterTests.cs
git commit -m "feat(filter): make IMessageDeduplicationPersistor async; bump MongoDB driver

- IMessageDeduplicationPersistor methods become async with CT.
- InMemory persistor wraps sync ops; CT honored pre-op.
- MongoDb persistor uses driver-native FirstOrDefaultAsync/InsertOneAsync/
  DeleteManyAsync; inner try/catch/log.Fatal/return blocks removed so
  exceptions propagate to the filter (fail-closed prerequisite).
- mongocsharpdriver 2.4.4 -> MongoDB.Driver 2.23.1 (aligns with Group B).
- Legacy OutgoingFilter/IncomingFilter helpers temporarily bridged via
  GetAwaiter().GetResult(); Task 3 will delete them.
- New MessageDeduplicationPersistorInMemoryTests (6 tests).
- Existing Moq setups in OutgoingFilterTests and IncomingFilterTests updated.

Addresses part of R-017/R-018."
```

---

## Task 3: Collapse Legacy Helpers Into Wrapper Filters; Apply Fail-Closed Policy

**Rationale:** Delete the internal `OutgoingFilter` / `IncomingFilter` singleton helpers. Inline their logic directly into `OutgoingDeduplicationFilter` / `IncomingDeduplicationFilter`. The outgoing filter now **fails closed** — no try/catch around the persistor call. Cleanup timer logic also moves out (the current `OutgoingFilter.Callback` + timer field is going away; the new `DeduplicationCleanupHostedService` in Task 5 takes over). Wrapper filters still use `DeduplicationFilterSettings.Instance` and `PersistorFactory.Create` for now — Task 4 migrates them to DI.

**Files:**
- Rewrite: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilter.cs`
- Rewrite: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilter.cs`
- Delete: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingFilter.cs`
- Delete: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingFilter.cs`
- Rewrite: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/OutgoingFilterTests.cs`
- Rewrite: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/IncomingFilterTests.cs`

- [ ] **Step 1: Write the failing OutgoingDeduplicationFilter tests**

Replace the entire content of `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/OutgoingFilterTests.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Filters.MessageDeduplication.Filters;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.Filters.MessageDeduplication.Tests
{
    public class OutgoingDeduplicationFilterTests
    {
        private readonly Mock<IMessageDeduplicationPersistor> _persistor = new();

        private OutgoingDeduplicationFilter CreateFilter()
        {
            // Constructor injects the persistor directly in the post-DI world (Task 4).
            // For this task, the filter still resolves IMessageDeduplicationPersistor via
            // a Lazy<> factory holding a static field set up for test through
            // OutgoingDeduplicationFilter.OverridePersistorForTesting(_persistor.Object).
            OutgoingDeduplicationFilter.OverridePersistorForTesting(_persistor.Object);
            return new OutgoingDeduplicationFilter();
        }

        private static Envelope EnvelopeWithMessageId(Guid id)
        {
            return new Envelope
            {
                Headers = new Dictionary<string, object>
                {
                    { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) }
                }
            };
        }

        [Fact]
        public async Task ProcessAsync_HappyPath_CallsInsertAndReturnsTrue()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.InsertAsync(id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var filter = CreateFilter();
            var result = await filter.ProcessAsync(EnvelopeWithMessageId(id));

            Assert.True(result);
            _persistor.VerifyAll();
        }

        [Fact]
        public async Task ProcessAsync_PersistorThrows_ExceptionPropagates()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.InsertAsync(id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("boom"));

            var filter = CreateFilter();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                filter.ProcessAsync(EnvelopeWithMessageId(id)));
        }

        [Fact]
        public async Task ProcessAsync_PreCancelledToken_ThrowsOCE()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.InsertAsync(id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var filter = CreateFilter();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                filter.ProcessAsync(EnvelopeWithMessageId(id), cts.Token));
        }
    }
}
```

Note: `OverridePersistorForTesting` is a test seam we are intentionally adding to the non-DI-yet filter. In Task 4 we delete it along with the static fallback when constructor injection takes over.

- [ ] **Step 2: Write the failing IncomingDeduplicationFilter tests**

Replace the entire content of `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/IncomingFilterTests.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Filters.MessageDeduplication.Filters;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.Filters.MessageDeduplication.Tests
{
    public class IncomingDeduplicationFilterTests
    {
        private readonly Mock<IMessageDeduplicationPersistor> _persistor = new();

        private IncomingDeduplicationFilter CreateFilter()
        {
            IncomingDeduplicationFilter.OverridePersistorForTesting(_persistor.Object);
            return new IncomingDeduplicationFilter();
        }

        [Fact]
        public async Task ProcessAsync_NotRedelivered_ReturnsTrue()
        {
            var filter = CreateFilter();
            var envelope = new Envelope { Headers = new Dictionary<string, object>() };

            var result = await filter.ProcessAsync(envelope);

            Assert.True(result);
            _persistor.Verify(p => p.GetMessageExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ProcessAsync_RedeliveredButNotInPersistor_ReturnsTrue()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.GetMessageExistsAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            var filter = CreateFilter();
            var envelope = new Envelope
            {
                Headers = new Dictionary<string, object>
                {
                    { "Redelivered", true },
                    { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) }
                }
            };

            var result = await filter.ProcessAsync(envelope);

            Assert.True(result);
        }

        [Fact]
        public async Task ProcessAsync_RedeliveredAndInPersistor_ReturnsFalse()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.GetMessageExistsAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var filter = CreateFilter();
            var envelope = new Envelope
            {
                Headers = new Dictionary<string, object>
                {
                    { "Redelivered", true },
                    { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) }
                }
            };

            var result = await filter.ProcessAsync(envelope);

            Assert.False(result);
        }

        [Fact]
        public async Task ProcessAsync_PersistorThrows_ExceptionPropagates()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.GetMessageExistsAsync(id, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("boom"));

            var filter = CreateFilter();
            var envelope = new Envelope
            {
                Headers = new Dictionary<string, object>
                {
                    { "Redelivered", true },
                    { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) }
                }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => filter.ProcessAsync(envelope));
        }

        [Fact]
        public async Task ProcessAsync_PreCancelledToken_ThrowsOCE()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.GetMessageExistsAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            var filter = CreateFilter();
            var envelope = new Envelope
            {
                Headers = new Dictionary<string, object>
                {
                    { "Redelivered", true },
                    { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) }
                }
            };

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                filter.ProcessAsync(envelope, cts.Token));
        }
    }
}
```

- [ ] **Step 3: Run the tests to confirm they fail to compile**

```bash
dotnet test filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/ServiceConnect.Filters.MessageDeduplication.Tests.csproj
```

Expected: build fails — `OverridePersistorForTesting` does not exist on the filters.

- [ ] **Step 4: Rewrite `OutgoingDeduplicationFilter.cs` with inlined logic and fail-closed policy**

Replace the entire body of `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilter.cs` with:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class OutgoingDeduplicationFilter : IFilter
    {
        private static IMessageDeduplicationPersistor? _overridePersistor;
        private static readonly Lazy<IMessageDeduplicationPersistor> _defaultPersistor = new(() =>
            PersistorFactory.Create(DeduplicationFilterSettings.Instance.PersistorType));

        public IBus Bus { get; set; }

        private static IMessageDeduplicationPersistor Persistor => _overridePersistor ?? _defaultPersistor.Value;

        // Internal test seam — removed in Task 4 when DI takes over.
        internal static void OverridePersistorForTesting(IMessageDeduplicationPersistor persistor) => _overridePersistor = persistor;

        public async Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var messageId = new Guid(HeaderDecoder.Decode(envelope.Headers["MessageId"]) ?? string.Empty);
            var expiry = DateTime.UtcNow.AddHours(DeduplicationFilterSettings.Instance.MsgExpiryHours);

            // Fail-closed: no try/catch. If InsertAsync throws, the send fails and
            // the caller can retry. Silently swallowing would break the dedup guarantee.
            await Persistor.InsertAsync(messageId, expiry, cancellationToken).ConfigureAwait(false);

            return true; // continue pipeline (true = continue, false = block)
        }
    }
}
```

- [ ] **Step 5: Rewrite `IncomingDeduplicationFilter.cs` with inlined logic**

Replace the entire body with:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class IncomingDeduplicationFilter : IFilter
    {
        private static IMessageDeduplicationPersistor? _overridePersistor;
        private static readonly Lazy<IMessageDeduplicationPersistor> _defaultPersistor = new(() =>
            PersistorFactory.Create(DeduplicationFilterSettings.Instance.PersistorType));

        public IBus Bus { get; set; }

        private static IMessageDeduplicationPersistor Persistor => _overridePersistor ?? _defaultPersistor.Value;

        internal static void OverridePersistorForTesting(IMessageDeduplicationPersistor persistor) => _overridePersistor = persistor;

        public async Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // RabbitMQ guarantees a first-delivery has never been seen before; skip the check.
            // https://www.rabbitmq.com/reliability.html
            if (!envelope.Headers.ContainsKey("Redelivered"))
                return true;

            if (!bool.TryParse(HeaderDecoder.Decode(envelope.Headers["Redelivered"]), out var redelivered) || !redelivered)
                return true;

            var messageId = new Guid(HeaderDecoder.Decode(envelope.Headers["MessageId"]) ?? string.Empty);
            var exists = await Persistor.GetMessageExistsAsync(messageId, cancellationToken).ConfigureAwait(false);

            // exists = duplicate -> block (false). Otherwise continue (true).
            return !exists;
        }
    }
}
```

- [ ] **Step 6: Delete the legacy helper files**

```bash
git rm filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingFilter.cs
git rm filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingFilter.cs
```

- [ ] **Step 7: Run build + tests**

```bash
dotnet build
dotnet test --filter "Category!=Docker"
```

Expected: build succeeds. New `OutgoingDeduplicationFilterTests` (3 tests) and `IncomingDeduplicationFilterTests` (5 tests) pass. All prior unit tests still pass.

- [ ] **Step 8: Commit**

```bash
git add filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/ \
    filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/OutgoingFilterTests.cs \
    filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/IncomingFilterTests.cs
git commit -m "feat(filter): inline legacy helpers into wrappers; outgoing dedup is fail-closed

- OutgoingDeduplicationFilter + IncomingDeduplicationFilter now hold the
  logic directly. No more delegation to Lazy<OutgoingFilter>/Lazy<IncomingFilter>.
- Outgoing dedup: no try/catch around InsertAsync; persistor exceptions
  propagate to the caller. Dedup guarantee is honored under failure.
- Incoming dedup: unchanged behavior (redelivery-only check; exists = block).
- Legacy OutgoingFilter.cs + IncomingFilter.cs deleted (their cleanup timer
  also removed; DeduplicationCleanupHostedService replaces it in Task 5).
- Tests rewritten: OutgoingFilterTests, IncomingFilterTests now target the
  public wrapper filters; OverridePersistorForTesting test seam added
  (removed in Task 4 when DI replaces the static persistor fallback).

Addresses R-017 (no more silent swallow on outgoing)."
```

---

## Task 4: Settings → POCO/IOptions + AddMessageDeduplicationFilter Extension; Delete PersistorFactory

**Rationale:** Demote `DeduplicationFilterSettings` from singleton to plain options class. Delete `PersistorFactory` (its switch moves into the extension method). Filters gain constructor injection of `IMessageDeduplicationPersistor` + `IOptions<DeduplicationFilterSettings>` and lose their `Lazy<T>`/`OverridePersistorForTesting` scaffolding. New extension method registers everything.

**Files:**
- Rewrite: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationFilterSettings.cs`
- Rewrite: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilter.cs`
- Rewrite: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilter.cs`
- Rewrite: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorMongoDb.cs` (constructor takes options directly)
- Delete: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/PersistorFactory.cs`
- Delete: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/PersistorFactoryTests.cs`
- Create: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/AddMessageDeduplicationFilterExtensions.cs`
- Create: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/AddMessageDeduplicationFilterTests.cs`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.csproj` (add DI packages)
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/ServiceConnect.Filters.MessageDeduplication.Tests.csproj` (add DI package)
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/OutgoingFilterTests.cs` (remove OverridePersistorForTesting, pass persistor + options via ctor)
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/IncomingFilterTests.cs` (same)

- [ ] **Step 1: Add NuGet package references**

Edit `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.csproj`. Add inside the existing `<ItemGroup>` that contains PackageReferences:

```xml
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="9.0.0" />
```

Edit `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/ServiceConnect.Filters.MessageDeduplication.Tests.csproj`. Add inside the PackageReference `<ItemGroup>`:

```xml
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="9.0.0" />
```

- [ ] **Step 2: Write the failing AddMessageDeduplicationFilter registration tests**

Create `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/AddMessageDeduplicationFilterTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ServiceConnect.Filters.MessageDeduplication;
using ServiceConnect.Filters.MessageDeduplication.Filters;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using Xunit;

namespace ServiceConnect.Filters.MessageDeduplication.Tests
{
    public class AddMessageDeduplicationFilterTests
    {
        [Fact]
        public void InMemoryType_ResolvesInMemoryPersistor()
        {
            var services = new ServiceCollection();
            services.AddMessageDeduplicationFilter(cfg =>
            {
                cfg.PersistorType = PersistorType.InMemory;
            });

            var provider = services.BuildServiceProvider();
            var persistor = provider.GetRequiredService<IMessageDeduplicationPersistor>();

            Assert.IsType<MessageDeduplicationPersistorInMemory>(persistor);
        }

        [Fact]
        public void RegistersOptions_BoundToUserConfiguration()
        {
            var services = new ServiceCollection();
            services.AddMessageDeduplicationFilter(cfg =>
            {
                cfg.PersistorType = PersistorType.InMemory;
                cfg.MsgExpiryHours = 48;
                cfg.MsgCleanupIntervalMinutes = 5;
            });

            var provider = services.BuildServiceProvider();
            var opts = provider.GetRequiredService<IOptions<DeduplicationFilterSettings>>().Value;

            Assert.Equal(48, opts.MsgExpiryHours);
            Assert.Equal(5, opts.MsgCleanupIntervalMinutes);
        }

        [Fact]
        public void RegistersBothFilters()
        {
            var services = new ServiceCollection();
            services.AddMessageDeduplicationFilter(cfg => { cfg.PersistorType = PersistorType.InMemory; });

            var provider = services.BuildServiceProvider();

            Assert.NotNull(provider.GetRequiredService<OutgoingDeduplicationFilter>());
            Assert.NotNull(provider.GetRequiredService<IncomingDeduplicationFilter>());
        }

        [Fact]
        public void RegistersCleanupHostedService()
        {
            var services = new ServiceCollection();
            services.AddMessageDeduplicationFilter(cfg => { cfg.PersistorType = PersistorType.InMemory; });

            Assert.Contains(services, d =>
                d.ServiceType == typeof(IHostedService) &&
                d.ImplementationType == typeof(DeduplicationCleanupHostedService));
        }

        [Fact]
        public void NullConfigure_Throws()
        {
            var services = new ServiceCollection();
            Assert.Throws<System.ArgumentNullException>(() =>
                services.AddMessageDeduplicationFilter(null!));
        }
    }
}
```

- [ ] **Step 3: Run the test (should fail to compile — `AddMessageDeduplicationFilter` and `DeduplicationCleanupHostedService` don't exist yet)**

```bash
dotnet test filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/
```

Expected: build fails with "AddMessageDeduplicationFilter is not an extension method" and similar.

- [ ] **Step 4: Rewrite `DeduplicationFilterSettings.cs` as POCO**

Replace the entire body of `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationFilterSettings.cs` with:

```csharp
namespace ServiceConnect.Filters.MessageDeduplication
{
    /// <summary>
    /// Options for the message deduplication filter. Register via
    /// <see cref="AddMessageDeduplicationFilterExtensions.AddMessageDeduplicationFilter"/>.
    /// </summary>
    public sealed class DeduplicationFilterSettings
    {
        public int MsgExpiryHours { get; set; } = 24;

        /// <summary>
        /// How often to clean up expired messages from the persistance store.
        /// </summary>
        public int MsgCleanupIntervalMinutes { get; set; } = 60;

        /// <summary>
        /// MongoDb persistance store connection string.
        /// </summary>
        public string? ConnectionStringMongoDb { get; set; } = "mongodb://localhost";

        /// <summary>
        /// Name of the MongoDb database.
        /// </summary>
        public string? DatabaseNameMongoDb { get; set; } = "ServiceConnect-Filters-MessageDeduplication";

        /// <summary>
        /// Name of the MongoDb collection.
        /// </summary>
        public string? CollectionNameMongoDb { get; set; } = "ProcessedMessages";

        /// <summary>
        /// Path to X509 certificate file for MongoDB SSL client authentication.
        /// If set, TLS is auto-enabled on the connection.
        /// Takes precedence over MongoDbCertBase64 if both are set.
        /// </summary>
        public string? MongoDbCertPath { get; set; }

        /// <summary>
        /// Base64-encoded X509 certificate for MongoDB SSL client authentication.
        /// Alternative to MongoDbCertPath for environments where file paths are impractical.
        /// </summary>
        public string? MongoDbCertBase64 { get; set; }

        /// <summary>
        /// Password for the X509 certificate (optional).
        /// Used with both MongoDbCertPath and MongoDbCertBase64.
        /// </summary>
        public string? MongoDbCertPassphrase { get; set; }

        /// <summary>
        /// Which persistor backend to use for message deduplication.
        /// </summary>
        public PersistorType PersistorType { get; set; } = PersistorType.InMemory;

        /// <summary>
        /// Disable message expiry. Processed messages in the persistance store won't get deleted.
        /// </summary>
        public bool DisableMsgExpiry { get; set; }
    }
}
```

No `Instance`, no `Lazy<T>`, no private constructor.

- [ ] **Step 5: Rewrite `MessageDeduplicationPersistorMongoDb.cs` to accept settings via constructor**

Replace the constructor signature and body. The new constructor takes a `DeduplicationFilterSettings` directly instead of reading `DeduplicationFilterSettings.Instance`:

```csharp
using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors
{
    public class MessageDeduplicationPersistorMongoDb : IMessageDeduplicationPersistor
    {
        private readonly IMongoCollection<ProcessedMessage> _collection;

        public MessageDeduplicationPersistorMongoDb(DeduplicationFilterSettings settings)
        {
            var url = new MongoUrl(settings.ConnectionStringMongoDb);
            var clientSettings = MongoClientSettings.FromUrl(url);

            if (!string.IsNullOrEmpty(settings.MongoDbCertPath) ||
                !string.IsNullOrEmpty(settings.MongoDbCertBase64))
            {
                X509Certificate2 cert;
                if (!string.IsNullOrEmpty(settings.MongoDbCertPath))
                {
                    cert = string.IsNullOrEmpty(settings.MongoDbCertPassphrase)
                        ? new X509Certificate2(settings.MongoDbCertPath)
                        : new X509Certificate2(settings.MongoDbCertPath, settings.MongoDbCertPassphrase);
                }
                else
                {
                    var certBytes = Convert.FromBase64String(settings.MongoDbCertBase64!);
                    cert = string.IsNullOrEmpty(settings.MongoDbCertPassphrase)
                        ? new X509Certificate2(certBytes)
                        : new X509Certificate2(certBytes, settings.MongoDbCertPassphrase);
                }

                clientSettings.UseTls = true;
                clientSettings.SslSettings = new SslSettings
                {
                    ClientCertificates = new List<X509Certificate> { cert },
                    ClientCertificateSelectionCallback = (sender, host, certificates, certificate, issuers) => certificates[0],
                    CheckCertificateRevocation = true
                };
            }

            var mongoClient = new MongoClient(clientSettings);
            var mongoDatabase = mongoClient.GetDatabase(settings.DatabaseNameMongoDb);
            _collection = mongoDatabase.GetCollection<ProcessedMessage>(settings.CollectionNameMongoDb);

            _collection.Indexes.CreateOneAsync(
                new CreateIndexModel<ProcessedMessage>(Builders<ProcessedMessage>.IndexKeys.Ascending(_ => _.Id)));
            _collection.Indexes.CreateOneAsync(
                new CreateIndexModel<ProcessedMessage>(Builders<ProcessedMessage>.IndexKeys.Ascending(_ => _.ExpiryDateTime)));
        }

        public async Task<bool> GetMessageExistsAsync(Guid messageId, CancellationToken cancellationToken = default)
        {
            var found = await _collection.Find(i => i.Id == messageId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return found != null;
        }

        public Task InsertAsync(Guid messageId, DateTime messageExpiry, CancellationToken cancellationToken = default)
        {
            return _collection.InsertOneAsync(
                new ProcessedMessage { Id = messageId, ExpiryDateTime = messageExpiry },
                options: null,
                cancellationToken: cancellationToken);
        }

        public Task RemoveExpiredMessagesAsync(DateTime messageExpiry, CancellationToken cancellationToken = default)
        {
            return _collection.DeleteManyAsync(i => i.ExpiryDateTime < messageExpiry, cancellationToken);
        }
    }
}
```

- [ ] **Step 6: Rewrite filters with constructor injection**

Replace the entire body of `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilter.cs` with:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class OutgoingDeduplicationFilter : IFilter
    {
        private readonly IMessageDeduplicationPersistor _persistor;
        private readonly DeduplicationFilterSettings _settings;

        public IBus Bus { get; set; } = null!;

        public OutgoingDeduplicationFilter(
            IMessageDeduplicationPersistor persistor,
            IOptions<DeduplicationFilterSettings> options)
        {
            _persistor = persistor ?? throw new ArgumentNullException(nameof(persistor));
            _settings = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var messageId = new Guid(HeaderDecoder.Decode(envelope.Headers["MessageId"]) ?? string.Empty);
            var expiry = DateTime.UtcNow.AddHours(_settings.MsgExpiryHours);

            // Fail-closed: no try/catch. If InsertAsync throws, the send fails and
            // the caller can retry. Silently swallowing would break the dedup guarantee.
            await _persistor.InsertAsync(messageId, expiry, cancellationToken).ConfigureAwait(false);

            return true; // continue pipeline (true = continue, false = block)
        }
    }
}
```

Replace `IncomingDeduplicationFilter.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class IncomingDeduplicationFilter : IFilter
    {
        private readonly IMessageDeduplicationPersistor _persistor;

        public IBus Bus { get; set; } = null!;

        public IncomingDeduplicationFilter(IMessageDeduplicationPersistor persistor)
        {
            _persistor = persistor ?? throw new ArgumentNullException(nameof(persistor));
        }

        public async Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!envelope.Headers.ContainsKey("Redelivered"))
                return true;

            if (!bool.TryParse(HeaderDecoder.Decode(envelope.Headers["Redelivered"]), out var redelivered) || !redelivered)
                return true;

            var messageId = new Guid(HeaderDecoder.Decode(envelope.Headers["MessageId"]) ?? string.Empty);
            var exists = await _persistor.GetMessageExistsAsync(messageId, cancellationToken).ConfigureAwait(false);

            return !exists;
        }
    }
}
```

- [ ] **Step 7: Create `AddMessageDeduplicationFilterExtensions.cs`**

Create `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/AddMessageDeduplicationFilterExtensions.cs`:

```csharp
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ServiceConnect.Filters.MessageDeduplication.Filters;
using ServiceConnect.Filters.MessageDeduplication.Persistors;

namespace ServiceConnect.Filters.MessageDeduplication
{
    public static class AddMessageDeduplicationFilterExtensions
    {
        /// <summary>
        /// Registers the message deduplication filter, its persistor (based on
        /// <see cref="DeduplicationFilterSettings.PersistorType"/>), and the background
        /// cleanup service with the provided service collection.
        /// </summary>
        public static IServiceCollection AddMessageDeduplicationFilter(
            this IServiceCollection services,
            Action<DeduplicationFilterSettings> configure)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));
            if (configure is null) throw new ArgumentNullException(nameof(configure));

            services.Configure(configure);

            services.AddSingleton<IMessageDeduplicationPersistor>(sp =>
            {
                var settings = sp.GetRequiredService<IOptions<DeduplicationFilterSettings>>().Value;
                return settings.PersistorType switch
                {
                    PersistorType.InMemory => new MessageDeduplicationPersistorInMemory(),
                    PersistorType.MongoDb  => new MessageDeduplicationPersistorMongoDb(settings),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(settings.PersistorType), settings.PersistorType, "Unsupported persistor type.")
                };
            });

            services.AddTransient<OutgoingDeduplicationFilter>();
            services.AddTransient<IncomingDeduplicationFilter>();
            services.AddHostedService<DeduplicationCleanupHostedService>();

            return services;
        }
    }
}
```

Note: `DeduplicationCleanupHostedService` is referenced here but not yet implemented. That makes Task 4 depend on the class existing. Approach: **introduce a minimal placeholder** here, which Task 5 fleshes out. To keep the build green:

Create `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationCleanupHostedService.cs` with a placeholder:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace ServiceConnect.Filters.MessageDeduplication
{
    // Placeholder — Task 5 implements the cleanup loop.
    public sealed class DeduplicationCleanupHostedService : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
}
```

- [ ] **Step 8: Delete `PersistorFactory.cs` and its test**

```bash
git rm filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/PersistorFactory.cs
git rm filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/PersistorFactoryTests.cs
```

- [ ] **Step 9: Update wrapper filter tests to use constructor injection**

In `OutgoingFilterTests.cs`, change the `CreateFilter` helper and remove the `OverridePersistorForTesting` call. Rewrite the whole file as:

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Moq;
using ServiceConnect.Filters.MessageDeduplication.Filters;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.Filters.MessageDeduplication.Tests
{
    public class OutgoingDeduplicationFilterTests
    {
        private readonly Mock<IMessageDeduplicationPersistor> _persistor = new();
        private readonly DeduplicationFilterSettings _settings = new() { MsgExpiryHours = 24 };

        private OutgoingDeduplicationFilter CreateFilter() =>
            new(_persistor.Object, Options.Create(_settings));

        private static Envelope EnvelopeWithMessageId(Guid id) =>
            new() { Headers = new Dictionary<string, object> { { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) } } };

        [Fact]
        public async Task ProcessAsync_HappyPath_CallsInsertAndReturnsTrue()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.InsertAsync(id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var filter = CreateFilter();
            var result = await filter.ProcessAsync(EnvelopeWithMessageId(id));

            Assert.True(result);
            _persistor.VerifyAll();
        }

        [Fact]
        public async Task ProcessAsync_PersistorThrows_ExceptionPropagates()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.InsertAsync(id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("boom"));

            var filter = CreateFilter();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                filter.ProcessAsync(EnvelopeWithMessageId(id)));
        }

        [Fact]
        public async Task ProcessAsync_PreCancelledToken_ThrowsOCE()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.InsertAsync(id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var filter = CreateFilter();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                filter.ProcessAsync(EnvelopeWithMessageId(id), cts.Token));
        }
    }
}
```

Rewrite `IncomingFilterTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Filters.MessageDeduplication.Filters;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.Filters.MessageDeduplication.Tests
{
    public class IncomingDeduplicationFilterTests
    {
        private readonly Mock<IMessageDeduplicationPersistor> _persistor = new();

        private IncomingDeduplicationFilter CreateFilter() => new(_persistor.Object);

        [Fact]
        public async Task ProcessAsync_NotRedelivered_ReturnsTrue()
        {
            var filter = CreateFilter();
            var envelope = new Envelope { Headers = new Dictionary<string, object>() };

            var result = await filter.ProcessAsync(envelope);

            Assert.True(result);
            _persistor.Verify(p => p.GetMessageExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ProcessAsync_RedeliveredButNotInPersistor_ReturnsTrue()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.GetMessageExistsAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(false);

            var filter = CreateFilter();
            var envelope = new Envelope
            {
                Headers = new Dictionary<string, object>
                {
                    { "Redelivered", true },
                    { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) }
                }
            };

            Assert.True(await filter.ProcessAsync(envelope));
        }

        [Fact]
        public async Task ProcessAsync_RedeliveredAndInPersistor_ReturnsFalse()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.GetMessageExistsAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

            var filter = CreateFilter();
            var envelope = new Envelope
            {
                Headers = new Dictionary<string, object>
                {
                    { "Redelivered", true },
                    { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) }
                }
            };

            Assert.False(await filter.ProcessAsync(envelope));
        }

        [Fact]
        public async Task ProcessAsync_PersistorThrows_ExceptionPropagates()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.GetMessageExistsAsync(id, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("boom"));

            var filter = CreateFilter();
            var envelope = new Envelope
            {
                Headers = new Dictionary<string, object>
                {
                    { "Redelivered", true },
                    { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) }
                }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => filter.ProcessAsync(envelope));
        }

        [Fact]
        public async Task ProcessAsync_PreCancelledToken_ThrowsOCE()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.GetMessageExistsAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(false);

            var filter = CreateFilter();
            var envelope = new Envelope
            {
                Headers = new Dictionary<string, object>
                {
                    { "Redelivered", true },
                    { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) }
                }
            };

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                filter.ProcessAsync(envelope, cts.Token));
        }
    }
}
```

- [ ] **Step 10: Run build + tests**

```bash
dotnet build
dotnet test --filter "Category!=Docker"
```

Expected: build succeeds. All unit tests pass: `AddMessageDeduplicationFilterTests` (5), `OutgoingDeduplicationFilterTests` (3), `IncomingDeduplicationFilterTests` (5), `MessageDeduplicationPersistorInMemoryTests` (6), plus the pre-existing `FilterPipelineTests` (9). `PersistorFactoryTests` is gone.

- [ ] **Step 11: Commit**

```bash
git add filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/ \
    filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/
git commit -m "feat(filter): DeduplicationFilterSettings -> IOptions; add DI extension; delete PersistorFactory

- DeduplicationFilterSettings becomes a plain POCO (no singleton,
  no Lazy<T>, no private ctor).
- AddMessageDeduplicationFilter(cfg => ...) extension registers settings
  via IOptions<T>, selects the persistor based on PersistorType, and
  registers both wrapper filters and the cleanup hosted service.
- PersistorFactory static class deleted -- its switch logic moves inside
  the extension method's IServiceProvider callback.
- Filters now receive IMessageDeduplicationPersistor + IOptions via
  constructor injection. OverridePersistorForTesting seam removed.
- MessageDeduplicationPersistorMongoDb constructor takes settings
  directly instead of reading DeduplicationFilterSettings.Instance.
- DeduplicationCleanupHostedService placeholder added (Task 5 fills in).
- PersistorFactoryTests deleted; 5 new AddMessageDeduplicationFilterTests.

Addresses R-032."
```

---

## Task 5: DeduplicationCleanupHostedService

**Rationale:** Periodic cleanup moves out of the legacy `OutgoingFilter.Callback` timer into a proper `BackgroundService`. Respects `DisableMsgExpiry`, honors `stoppingToken`, logs and continues on transient exceptions (distinct from the filter-path fail-closed policy since cleanup has no caller).

**Files:**
- Rewrite: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationCleanupHostedService.cs`
- Create: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/DeduplicationCleanupHostedServiceTests.cs`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.csproj` (add Microsoft.Extensions.Logging.Abstractions)
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/ServiceConnect.Filters.MessageDeduplication.Tests.csproj` (add logging packages)

- [ ] **Step 1: Add logging package references**

Edit `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.csproj`. Add inside the PackageReferences `<ItemGroup>`:

```xml
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
```

Edit `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/ServiceConnect.Filters.MessageDeduplication.Tests.csproj`. Add inside the PackageReferences `<ItemGroup>`:

```xml
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
```

- [ ] **Step 2: Write the failing hosted service tests**

Create `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/DeduplicationCleanupHostedServiceTests.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ServiceConnect.Filters.MessageDeduplication;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using Xunit;

namespace ServiceConnect.Filters.MessageDeduplication.Tests
{
    public class DeduplicationCleanupHostedServiceTests
    {
        private readonly Mock<IMessageDeduplicationPersistor> _persistor = new();

        private DeduplicationCleanupHostedService CreateService(DeduplicationFilterSettings settings)
        {
            return new DeduplicationCleanupHostedService(
                _persistor.Object,
                Options.Create(settings),
                NullLogger<DeduplicationCleanupHostedService>.Instance);
        }

        [Fact]
        public async Task DisableMsgExpiryTrue_NeverCallsPersistor()
        {
            var svc = CreateService(new DeduplicationFilterSettings
            {
                DisableMsgExpiry = true,
                MsgCleanupIntervalMinutes = 1 // irrelevant; service returns immediately
            });

            using var cts = new CancellationTokenSource();
            await svc.StartAsync(cts.Token);
            await Task.Delay(100);
            cts.Cancel();
            await svc.StopAsync(CancellationToken.None);

            _persistor.Verify(p => p.RemoveExpiredMessagesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task DisableMsgExpiryFalse_CallsRemoveExpiredOnInterval()
        {
            var callCount = 0;
            _persistor.Setup(p => p.RemoveExpiredMessagesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Interlocked.Increment(ref callCount);
                    return Task.CompletedTask;
                });

            // MsgCleanupIntervalMinutes is in minutes (int), so the minimum is 1 minute — too long
            // for tests. We expose an internal TimeSpan constructor for tests.
            var svc = DeduplicationCleanupHostedService.CreateForTesting(
                _persistor.Object,
                disableMsgExpiry: false,
                interval: TimeSpan.FromMilliseconds(50),
                NullLogger<DeduplicationCleanupHostedService>.Instance);

            using var cts = new CancellationTokenSource();
            await svc.StartAsync(cts.Token);

            await Task.Delay(250); // allow a few intervals

            cts.Cancel();
            await svc.StopAsync(CancellationToken.None);

            Assert.True(callCount >= 2, $"Expected >= 2 calls, got {callCount}");
        }

        [Fact]
        public async Task StoppingTokenCancelled_StopsGracefullyWithoutThrow()
        {
            _persistor.Setup(p => p.RemoveExpiredMessagesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var svc = DeduplicationCleanupHostedService.CreateForTesting(
                _persistor.Object,
                disableMsgExpiry: false,
                interval: TimeSpan.FromMilliseconds(50),
                NullLogger<DeduplicationCleanupHostedService>.Instance);

            using var cts = new CancellationTokenSource();
            await svc.StartAsync(cts.Token);
            await Task.Delay(100);
            cts.Cancel();

            // Should not throw
            await svc.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task PersistorThrows_LogsErrorAndContinuesLoop()
        {
            var callCount = 0;
            _persistor.Setup(p => p.RemoveExpiredMessagesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Interlocked.Increment(ref callCount);
                    return Task.FromException(new InvalidOperationException("boom"));
                });

            var svc = DeduplicationCleanupHostedService.CreateForTesting(
                _persistor.Object,
                disableMsgExpiry: false,
                interval: TimeSpan.FromMilliseconds(50),
                NullLogger<DeduplicationCleanupHostedService>.Instance);

            using var cts = new CancellationTokenSource();
            await svc.StartAsync(cts.Token);

            await Task.Delay(250);

            cts.Cancel();
            await svc.StopAsync(CancellationToken.None);

            // Loop continued and retried despite exceptions.
            Assert.True(callCount >= 2, $"Expected >= 2 calls, got {callCount}");
        }
    }
}
```

- [ ] **Step 3: Run the test (should fail — `CreateForTesting` does not exist)**

```bash
dotnet test filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/
```

Expected: build fails on `CreateForTesting`.

- [ ] **Step 4: Implement `DeduplicationCleanupHostedService.cs`**

Replace the placeholder body with the full implementation:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceConnect.Filters.MessageDeduplication.Persistors;

namespace ServiceConnect.Filters.MessageDeduplication
{
    /// <summary>
    /// Periodically removes expired message ids from the deduplication persistor.
    /// Honors <see cref="DeduplicationFilterSettings.DisableMsgExpiry"/>.
    /// </summary>
    public sealed class DeduplicationCleanupHostedService : BackgroundService
    {
        private readonly IMessageDeduplicationPersistor _persistor;
        private readonly bool _disableMsgExpiry;
        private readonly TimeSpan _interval;
        private readonly ILogger<DeduplicationCleanupHostedService> _logger;

        public DeduplicationCleanupHostedService(
            IMessageDeduplicationPersistor persistor,
            IOptions<DeduplicationFilterSettings> options,
            ILogger<DeduplicationCleanupHostedService> logger)
            : this(
                persistor,
                (options ?? throw new ArgumentNullException(nameof(options))).Value.DisableMsgExpiry,
                TimeSpan.FromMinutes((options ?? throw new ArgumentNullException(nameof(options))).Value.MsgCleanupIntervalMinutes),
                logger)
        {
        }

        private DeduplicationCleanupHostedService(
            IMessageDeduplicationPersistor persistor,
            bool disableMsgExpiry,
            TimeSpan interval,
            ILogger<DeduplicationCleanupHostedService> logger)
        {
            _persistor = persistor ?? throw new ArgumentNullException(nameof(persistor));
            _disableMsgExpiry = disableMsgExpiry;
            _interval = interval;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // Internal test seam — allows sub-minute intervals for testing the loop.
        internal static DeduplicationCleanupHostedService CreateForTesting(
            IMessageDeduplicationPersistor persistor,
            bool disableMsgExpiry,
            TimeSpan interval,
            ILogger<DeduplicationCleanupHostedService> logger)
        {
            return new DeduplicationCleanupHostedService(persistor, disableMsgExpiry, interval, logger);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_disableMsgExpiry)
                return;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
                    await _persistor.RemoveExpiredMessagesAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return; // graceful shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during dedup cleanup; will retry on next interval");
                }
            }
        }
    }
}
```

`InternalsVisibleTo` for the test project: the tests live in `ServiceConnect.Filters.MessageDeduplication.Tests`. Check whether an `InternalsVisibleTo` attribute already exists in the main project; if not, add it.

Add a `Properties/AssemblyInfo.cs` fragment or use a `.csproj` `<ItemGroup>`. Prefer the csproj approach. Edit `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.csproj`, add:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="ServiceConnect.Filters.MessageDeduplication.Tests" />
  </ItemGroup>
```

- [ ] **Step 5: Build + run tests**

```bash
dotnet build
dotnet test --filter "Category!=Docker"
```

Expected: 4 new `DeduplicationCleanupHostedServiceTests` pass. All prior tests still green.

- [ ] **Step 6: Commit**

```bash
git add filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationCleanupHostedService.cs \
    filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.csproj \
    filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/DeduplicationCleanupHostedServiceTests.cs \
    filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/ServiceConnect.Filters.MessageDeduplication.Tests.csproj
git commit -m "feat(filter): DeduplicationCleanupHostedService runs periodic expiry cleanup

BackgroundService replaces the old static Timer in the removed
OutgoingFilter helper. Honors DisableMsgExpiry, exits cleanly on
stoppingToken cancellation, logs + continues on transient persistor
exceptions (no caller to propagate to for this out-of-band loop).

InternalsVisibleTo added for the test seam CreateForTesting, which
allows sub-minute intervals in tests.

Addresses R-032 (cleanup scheduler migration)."
```

---

## Task 6: Wrap-Up — Update Docs + E2E Smoke

**Rationale:** Mark issues closed in the deferred-notes doc. Run the full E2E suite under Docker to confirm no regressions. No code changes other than docs.

**Files:**
- Modify: `docs/remaining-issues.md`

- [ ] **Step 1: Update `docs/remaining-issues.md`**

The current header says:

```
Issues verified against source code on 2026-04-12. R-016/B-01 (CancellationToken) and R-034 (race condition) completed in Group C-1 on 2026-04-13. Tackle remaining items after further discussion.
```

Change it to:

```
Issues verified against source code on 2026-04-12. R-016/B-01 (CancellationToken) and R-034 (race condition) completed in Group C-1 on 2026-04-13. R-017/R-018 (async filter pipeline + fail-closed dedup) and R-032 (settings DI) completed in Group C-2 on 2026-04-13. Tackle remaining items after further discussion.
```

In the "From Deferred Issues (Confirmed Real)" table, update rows for R-017/R-018, R-022 (already done, unchanged), R-032:

For R-017/R-018:

```
| R-017/R-018 | Error Handling | Silent exception swallowing in dedup filter persistors (OutgoingFilter, MongoDb persistors) | **Done** (Group C-2) — IFilter/pipeline async; outgoing dedup now fail-closed; MongoDb persistor inner swallows removed |
```

For R-032:

```
| R-032 | Architecture | DeduplicationFilterSettings singleton pattern | **Done** (Group C-2) — POCO + IOptions<T> + AddMessageDeduplicationFilter extension method; PersistorFactory removed; DeduplicationCleanupHostedService replaces static Timer |
```

- [ ] **Step 2: Run the full unit test suite one more time**

```bash
dotnet test --filter "Category!=Docker"
```

Expected: all unit tests pass (baseline 231 + new additions this group). Record the count for the commit message.

- [ ] **Step 3: Run the E2E tests under sg docker**

```bash
sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"
```

Expected: 73/73 passing (no regression from Group C-1 baseline). If any fail, investigate whether the async filter migration broke the E2E-scoped `TestDeduplicationFilter`. Fix by checking Task 1 Step 8.

- [ ] **Step 4: Commit**

```bash
git add docs/remaining-issues.md
git commit -m "docs: mark R-017/R-018 and R-032 as done (Group C-2)

Group C-2 delivered: async IFilter/IFilterPipeline with CancellationToken;
outgoing dedup filter is fail-closed on persistor exceptions; MongoDb
persistor inner swallows removed; DeduplicationFilterSettings migrated
to IOptions<T> DI with AddMessageDeduplicationFilter extension;
PersistorFactory deleted; new DeduplicationCleanupHostedService.

E2E suite confirmed green: 73/73 passing under sg docker.
Unit suite green including new test classes."
```

---

## Self-Review

- [x] **Spec coverage:**
  - Section 1 (architecture/approach): Tasks 1-5 collectively implement all three pillars (async IFilter, fail-closed dedup, settings DI). Task 6 is the documented wrap-up.
  - Section 1 (IFilter async signature): Task 1 Step 1.
  - Section 2 (IFilterPipeline async): Task 1 Steps 2, 3.
  - Section 3 (8 caller sites): Task 1 Steps 4, 5.
  - Section 4 (IMessageDeduplicationPersistor async): Task 2 Steps 3, 4, 5.
  - Section 5 (Fail-closed outgoing filter): Task 3 Step 4 (plus reinforced via DI in Task 4 Step 6).
  - Section 6 (Settings + DI): Task 4 Steps 4, 7.
  - Section 7 (Cleanup hosted service): Task 5 (full implementation).
  - Section 8 (usage example): implicitly exercised by Task 4 tests.
  - Testing Strategy: Task 1 Step 9 (pipeline tests), Task 2 Step 1 (InMemory tests), Task 3 Steps 1, 2 (wrapper filter tests), Task 4 Step 2 (extension tests), Task 5 Step 2 (hosted service tests).
  - Wrap-up: Task 6.

- [x] **Placeholder scan:** No TBDs, TODOs, "add error handling" hand-waves. Every code step shows the full code body.

- [x] **Type consistency:**
  - `IMessageDeduplicationPersistor.InsertAsync / GetMessageExistsAsync / RemoveExpiredMessagesAsync` — used consistently across Tasks 2, 3, 4, 5.
  - `DeduplicationFilterSettings` POCO with nullable string properties — consistent between Task 4 Step 4 definition and Task 4 Step 5 consumer.
  - `IFilter.ProcessAsync` signature — consistent across Tasks 1, 3, 4.
  - `DeduplicationCleanupHostedService.CreateForTesting` — defined in Task 5 Step 4, used in Task 5 Step 2.
  - `OverridePersistorForTesting` — defined in Task 3 Steps 4, 5, removed in Task 4 Step 9 (test files) and Step 6 (filter files).

Plan complete.
