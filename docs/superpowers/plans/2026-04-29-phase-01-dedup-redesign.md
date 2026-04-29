# Phase 01 — Dedup-package removal + on-success filter stage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete `ServiceConnect.Filters.MessageDeduplication` entirely. Add an `OnConsumedSuccessfully` pipeline stage to the core so users can build their own dedup filter correctly. Ship a worked sample under `examples/CustomFilterAndMiddleware/` that demonstrates both the new stage and `IMessageProcessingMiddleware`.

**Architecture:** Additive change to `IPipelineConfiguration` / `IFilterPipeline` / `ServiceConnectBuilder` introducing a fourth pipeline stage that runs only after a successful handler invocation (`result.Success && !result.NotHandled`), invoked from inside `MessageDispatcher.DispatchAsync` before the existing `finally` block. Existing `BeforeConsuming` / `AfterConsuming` / `Outgoing` semantics are unchanged. The deleted dedup package is replaced by user-buildable patterns documented in the new sample and the website's `IFilter` reference page.

**Tech Stack:** .NET (multi-target net8.0/net10.0), xUnit + Moq for unit tests, Astro/Starlight for website docs, RabbitMQ via Docker for sample integration smoke. Test runner: `dotnet test` with `--filter` (per-csproj only — see [build/test safety](#buildtest-safety) below).

**Spec:** [`docs/superpowers/specs/2026-04-29-phase-01-dedup-redesign.md`](../specs/2026-04-29-phase-01-dedup-redesign.md).

---

## Build/test safety

This machine has crashed when running unconstrained whole-solution `dotnet build` / `dotnet test` against this codebase (CLAUDE.md has the full incident analysis). The wrapper at `~/.local/bin/dotnet` re-execs every `dotnet` invocation under a systemd cgroup with `CPUQuota=800%`, `MemoryMax=8G`, `MemorySwapMax=0`, `TasksMax=200` and is the safety net. **Even with the wrapper, every command in this plan is per-csproj.** Never run a whole-solution `dotnet build` / `dotnet test` / `dotnet format`. If a build/test hits the cgroup's 8 GB or 200-task ceiling, fix the build, do not lift the fence.

## File structure

### Modified — core pipeline (Group A)

- `src/ServiceConnect.Interfaces/Configuration/IPipelineConfiguration.cs` — add `OnConsumedSuccessfullyFilters` property.
- `src/ServiceConnect.Interfaces/Pipelines/IFilterPipeline.cs` — add `ExecuteOnConsumedSuccessfullyFiltersAsync` method.
- `src/ServiceConnect/Configuration/PipelineConfiguration.cs` — add the backing list + property pair.
- `src/ServiceConnect/Services/FilterPipeline.cs` — add the new pipeline-stage implementation routed through the existing `ExecuteFiltersAsync` helper.
- `src/ServiceConnect/ServiceConnectBuilder.cs` — add the `AddOnConsumedSuccessfullyFilter<T>()` builder method.
- `src/ServiceConnect/Services/MessageDispatcher.cs` — invoke the new stage in the success path before returning.
- `src/ServiceConnect.UnitTests/FilterPipelineTests.cs` — extend the existing fixture with new-stage tests.
- `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs` — extend the existing fixture with new-stage dispatcher-integration tests.
- `src/ServiceConnect.UnitTests/ServiceConnectBuilderTests.cs` — extend the existing fixture with the builder-method test.

### Deleted — dedup package + tests (Group B)

- `src/ServiceConnect.Filters.MessageDeduplication/` (entire directory).
- `src/ServiceConnect.UnitTests/Filters/MessageDeduplication/` (entire directory, 5 files).
- `src/ServiceConnect.slnx` line 8–10 (`<Folder Name="/Filters/">` block).

### Created/replaced — sample (Group C)

- Delete `examples/MessageDeduplication/`.
- Create `examples/CustomFilterAndMiddleware/` with:
  - `README.md`
  - `run.sh`, `run.ps1`
  - `CustomFilterAndMiddleware.sln`
  - `src/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts/Contracts.csproj`
  - `src/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts/OrderPlaced.cs`
  - `src/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender/Sender.csproj`
  - `src/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender/Program.cs`
  - `src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Consumer.csproj`
  - `src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Program.cs`
  - `src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/OrderPlacedHandler.cs`
  - `src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Filters/IDedupePersistor.cs`
  - `src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Filters/InMemoryDedupePersistor.cs`
  - `src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Filters/DedupeIncomingFilter.cs`
  - `src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Filters/DedupeOnSuccessFilter.cs`
  - `src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Middleware/LoggingTimingMiddleware.cs`

### Modified — documentation + website (Group D)

- `website/src/content/docs/reference/filters/ifilter.mdx` — add fourth pipeline-stage entry; rewrite worked example to split before-check from on-success-record.
- `website/src/content/docs/reference/configuration/ipipelineconfiguration.mdx` — add `OnConsumedSuccessfullyFilters`.
- `website/src/content/docs/learn/operations/idempotency.mdx` — rewrite "infrastructure-level belt-and-braces" section.
- `website/src/content/docs/learn/messaging-patterns/filters.mdx` — verify and update if it enumerates stages.
- `website/src/content/docs/samples.mdx` — replace `### MessageDeduplication` block with `### CustomFilterAndMiddleware`.
- `website/src/content/docs/releases.mdx` — v7 entry: package removal + new stage.
- `website/astro.config.mjs` — remove sidebar entry at line 127.
- `website/src/content/docs/reference/filters/messagededuplication.mdx` — **delete**.
- `README.md` (repo root) — search and update mentions.
- `examples/README.md` — rename entry, refresh description.

---

## Group A — Add the `OnConsumedSuccessfully` pipeline stage

### Task 1: Extend `IPipelineConfiguration` and `PipelineConfiguration`

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Configuration/IPipelineConfiguration.cs`
- Modify: `src/ServiceConnect/Configuration/PipelineConfiguration.cs`

- [ ] **Step 1: Add the interface property**

In `src/ServiceConnect.Interfaces/Configuration/IPipelineConfiguration.cs`, after the existing `AfterConsumingFilters` property:

```csharp
    /// <summary>
    /// Gets the filters that run only after a successful handler invocation
    /// (the dispatcher chain returned <see cref="ConsumeEventResult.Success"/> = true
    /// and <see cref="ConsumeEventResult.NotHandled"/> = false). Filters in this stage
    /// observe successful consumption only; failures and unhandled messages skip them.
    /// </summary>
    IReadOnlyList<Type> OnConsumedSuccessfullyFilters { get; }
```

- [ ] **Step 2: Add the backing list and properties to the implementation**

In `src/ServiceConnect/Configuration/PipelineConfiguration.cs`, mirror the existing pattern. Add the field alongside the others:

```csharp
    private readonly List<Type> _onConsumedSuccessfullyFilters = [];
```

Add the public `IList<Type>` property next to the existing pairs:

```csharp
    /// <summary>
    /// Gets the filters that run only after a successful handler invocation.
    /// </summary>
    public IList<Type> OnConsumedSuccessfullyFilters => _onConsumedSuccessfullyFilters;
```

Add the explicit interface implementation alongside the others:

```csharp
    IReadOnlyList<Type> IPipelineConfiguration.OnConsumedSuccessfullyFilters => _onConsumedSuccessfullyFilters;
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj
dotnet build src/ServiceConnect/ServiceConnect.csproj
```

Expected: both succeed.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Interfaces/Configuration/IPipelineConfiguration.cs \
        src/ServiceConnect/Configuration/PipelineConfiguration.cs
git commit -m "feat(filters): add OnConsumedSuccessfullyFilters to pipeline config"
```

---

### Task 2: Extend `IFilterPipeline` and `FilterPipeline`

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Pipelines/IFilterPipeline.cs`
- Modify: `src/ServiceConnect/Services/FilterPipeline.cs`
- Modify: `src/ServiceConnect.UnitTests/FilterPipelineTests.cs`

- [ ] **Step 1: Write the failing test in `FilterPipelineTests.cs`**

Append to the existing fixture (after the `ExecuteAfterConsumingFiltersAsync_*` block, mirroring the `Outgoing`-style tests):

```csharp
    [Fact]
    public async Task ExecuteOnConsumedSuccessfullyFiltersAsync_WithNoFilters_ReturnsContinue()
    {
        var envelope = new Envelope();
        var result = await _pipeline.ExecuteOnConsumedSuccessfullyFiltersAsync(envelope);
        Assert.Equal(FilterAction.Continue, result);
    }

    [Fact]
    public async Task ExecuteOnConsumedSuccessfullyFiltersAsync_WhenFilterContinues_PipelineContinues()
    {
        var mockFilter = new Mock<FakeFilter1>();
        mockFilter.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Continue);

        _mockServiceProvider
            .Setup(sp => sp.GetService(typeof(FakeFilter1)))
            .Returns(mockFilter.Object);

        _config.OnConsumedSuccessfullyFilters.Add(typeof(FakeFilter1));

        var envelope = new Envelope();
        var result = await _pipeline.ExecuteOnConsumedSuccessfullyFiltersAsync(envelope);

        Assert.Equal(FilterAction.Continue, result);
    }

    [Fact]
    public async Task ExecuteOnConsumedSuccessfullyFiltersAsync_WhenFilterStops_PipelineStops()
    {
        var mockFilter = new Mock<FakeFilter1>();
        mockFilter.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Stop);

        _mockServiceProvider
            .Setup(sp => sp.GetService(typeof(FakeFilter1)))
            .Returns(mockFilter.Object);

        _config.OnConsumedSuccessfullyFilters.Add(typeof(FakeFilter1));

        var envelope = new Envelope();
        var result = await _pipeline.ExecuteOnConsumedSuccessfullyFiltersAsync(envelope);

        Assert.Equal(FilterAction.Stop, result);
    }
```

- [ ] **Step 2: Run tests to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ExecuteOnConsumedSuccessfullyFiltersAsync"
```

Expected: compile error — `IFilterPipeline` does not contain `ExecuteOnConsumedSuccessfullyFiltersAsync`.

- [ ] **Step 3: Add the interface method**

In `src/ServiceConnect.Interfaces/Pipelines/IFilterPipeline.cs`, after `ExecuteAfterConsumingFiltersAsync`:

```csharp
    /// <summary>
    /// Executes all on-consumed-successfully filters. Returns
    /// <see cref="FilterAction.Stop"/> if any filter halted the pipeline;
    /// otherwise <see cref="FilterAction.Continue"/>. The dispatcher invokes this
    /// stage only after a successful handler — failures and unhandled messages
    /// skip it. <see cref="FilterAction.Stop"/> halts further on-success filters
    /// but does not flip the dispatch result to failure.
    /// </summary>
    Task<FilterAction> ExecuteOnConsumedSuccessfullyFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: Implement in `FilterPipeline`**

In `src/ServiceConnect/Services/FilterPipeline.cs`, mirror the existing methods. Insert after `ExecuteAfterConsumingFiltersAsync`:

```csharp
    /// <inheritdoc />
    public Task<FilterAction> ExecuteOnConsumedSuccessfullyFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        return ExecuteFiltersAsync(config.OnConsumedSuccessfullyFilters, envelope, cancellationToken);
    }
```

- [ ] **Step 5: Run tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ExecuteOnConsumedSuccessfullyFiltersAsync"
```

Expected: 3 passed.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Interfaces/Pipelines/IFilterPipeline.cs \
        src/ServiceConnect/Services/FilterPipeline.cs \
        src/ServiceConnect.UnitTests/FilterPipelineTests.cs
git commit -m "feat(filters): add ExecuteOnConsumedSuccessfullyFiltersAsync to FilterPipeline"
```

---

### Task 3: Add `AddOnConsumedSuccessfullyFilter<T>()` to `ServiceConnectBuilder`

**Files:**
- Modify: `src/ServiceConnect/ServiceConnectBuilder.cs`
- Modify: `src/ServiceConnect.UnitTests/ServiceConnectBuilderTests.cs`

- [ ] **Step 1: Write the failing test**

Open `src/ServiceConnect.UnitTests/ServiceConnectBuilderTests.cs`, find the test that exercises `AddAfterConsumingFilter` (search for `AfterConsumingFilters`). Add an analogous test:

```csharp
    [Fact]
    public void AddOnConsumedSuccessfullyFilter_AppendsTypeToConfig()
    {
        var services = new ServiceCollection();
        var builder = new ServiceConnectBuilder(services, new BusConfiguration());

        builder.AddOnConsumedSuccessfullyFilter<TestFilter>();

        Assert.Contains(typeof(TestFilter), builder.BusConfig.Pipeline.OnConsumedSuccessfullyFilters);
    }
```

If a `TestFilter` (or similar) class isn't already declared in this fixture, declare it inline in the test file alongside the existing test fakes:

```csharp
public sealed class TestFilter : IFilter
{
    public Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
        => Task.FromResult(FilterAction.Continue);
}
```

(Inspect the existing test file first — there's almost certainly already a fake; reuse it.)

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~AddOnConsumedSuccessfullyFilter"
```

Expected: compile error — `ServiceConnectBuilder` does not contain `AddOnConsumedSuccessfullyFilter`.

- [ ] **Step 3: Add the builder method**

In `src/ServiceConnect/ServiceConnectBuilder.cs`, after `AddAfterConsumingFilter<T>` (line 168):

```csharp
    /// <summary>
    /// Adds a filter that runs only after a successful handler invocation
    /// (the dispatcher chain returned <c>Success = true</c> and
    /// <c>NotHandled = false</c>). Failures and unhandled messages skip this stage.
    /// Use for at-most-once side effects that depend on the handler having
    /// completed — e.g. recording a deduplication key, publishing an audit
    /// event, writing to an outbox.
    /// </summary>
    /// <typeparam name="T">The filter type.</typeparam>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder AddOnConsumedSuccessfullyFilter<T>() where T : class, IFilter
    {
        BusConfig.Pipeline.OnConsumedSuccessfullyFilters.Add(typeof(T));
        return this;
    }
```

- [ ] **Step 4: Run test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~AddOnConsumedSuccessfullyFilter"
```

Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/ServiceConnectBuilder.cs \
        src/ServiceConnect.UnitTests/ServiceConnectBuilderTests.cs
git commit -m "feat(filters): add AddOnConsumedSuccessfullyFilter builder method"
```

---

### Task 4: Wire `MessageDispatcher` to invoke the new stage on success

**Files:**
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs`
- Modify: `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs`

- [ ] **Step 1: Write the failing test for the success path**

Open `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs`. Find the existing `MessageDispatcherTests` class. Locate the `_mockFilterPipeline` setup (around line 67–73) and add the new stage's default setup *next to the existing two*:

```csharp
        _mockFilterPipeline.Setup(f => f.ExecuteOnConsumedSuccessfullyFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(FilterAction.Continue);
```

Add a new `[Fact]` to the class:

```csharp
    [Fact]
    public async Task DispatchAsync_OnHandlerSuccess_InvokesOnConsumedSuccessfullyFilters()
    {
        // Arrange — full setup mirroring an existing successful-dispatch test in this file.
        // Use whichever existing helper builds the dispatcher with FakeMessage1 + TestDispatchHandler;
        // search this file for a passing dispatch test such as "DispatchAsync_DispatchesHandler".

        // Act
        var result = await dispatcher.DispatchAsync(/* args identical to the existing happy-path test */);

        // Assert
        Assert.True(result.Success);
        Assert.False(result.NotHandled);
        _mockFilterPipeline.Verify(
            f => f.ExecuteOnConsumedSuccessfullyFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
```

Note: the implementing engineer should copy the arrange block from the existing happy-path test in this fixture (search the file for a passing `DispatchAsync_*` test that exercises a successful handler dispatch and reuse its setup verbatim). Do not invent a new harness shape.

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~DispatchAsync_OnHandlerSuccess_InvokesOnConsumedSuccessfullyFilters"
```

Expected: FAIL — `Verify` reports zero invocations.

- [ ] **Step 3: Wire the dispatcher**

In `src/ServiceConnect/Services/MessageDispatcher.cs`, find the chain-result line (currently around line 158, the line `return await chain(messageBytes, type!, message, headers, envelope, cancellationToken).ConfigureAwait(false);`). Replace it with:

```csharp
            var result = await chain(messageBytes, type!, message, headers, envelope, cancellationToken).ConfigureAwait(false);

            if (result.Success && !result.NotHandled)
            {
                // Stop returned by an on-success filter halts further on-success filters
                // (handled inside ExecuteFiltersAsync) but is not propagated here:
                // consumption already succeeded.
                await _filterPipeline.ExecuteOnConsumedSuccessfullyFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
            }

            return result;
```

The on-success call sits **inside** the existing `try` block. A throw from the new stage propagates into the existing `catch (Exception ex)` (currently line 160) and produces `Success = false, Exception = ex`. The existing `finally` block (currently line 173) is **unchanged** — `ExecuteAfterConsumingFiltersAsync` still runs on every path.

- [ ] **Step 4: Run test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~DispatchAsync_OnHandlerSuccess_InvokesOnConsumedSuccessfullyFilters"
```

Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/MessageDispatcher.cs \
        src/ServiceConnect.UnitTests/MessageDispatcherTests.cs
git commit -m "feat(dispatcher): invoke OnConsumedSuccessfully stage on handler success"
```

---

### Task 5: Test — on-success filters do NOT fire when the chain throws

**Files:**
- Modify: `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs`

- [ ] **Step 1: Write the test**

Add to `MessageDispatcherTests`:

```csharp
    [Fact]
    public async Task DispatchAsync_OnHandlerThrow_DoesNotInvokeOnConsumedSuccessfullyFilters()
    {
        // Arrange — handler throws. Reuse the existing pattern in this file
        // for "handler throws" tests: TestDispatchHandler accepts throwOnHandle.
        // (Search this file for `throwOnHandle` to find an existing example.)
        var thrown = new InvalidOperationException("handler boom");

        // ... full arrange identical to the existing "handler throws" test ...

        // Act
        var result = await dispatcher.DispatchAsync(/* args identical to the existing throw test */);

        // Assert
        Assert.False(result.Success);
        Assert.Same(thrown, result.Exception);
        _mockFilterPipeline.Verify(
            f => f.ExecuteOnConsumedSuccessfullyFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockFilterPipeline.Verify(
            f => f.ExecuteAfterConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Once); // existing finally-block behavior is unchanged
    }
```

- [ ] **Step 2: Run test**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~DispatchAsync_OnHandlerThrow_DoesNotInvokeOnConsumedSuccessfullyFilters"
```

Expected: PASS first try (the dispatcher wiring already gates on `result.Success`, and the chain throw never reaches the gate).

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.UnitTests/MessageDispatcherTests.cs
git commit -m "test(dispatcher): on-success filters skipped when handler throws"
```

---

### Task 6: Test — on-success filters do NOT fire when result is `NotHandled`

**Files:**
- Modify: `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs`

- [ ] **Step 1: Write the test**

Add to `MessageDispatcherTests`:

```csharp
    [Fact]
    public async Task DispatchAsync_WhenNotHandled_DoesNotInvokeOnConsumedSuccessfullyFilters()
    {
        // Arrange — dispatch a message type with no registered handler so RunProcessors
        // returns Success=true, NotHandled=true. Reuse the existing pattern in this file:
        // search for a test that asserts `result.NotHandled` (there is at least one).

        // ... full arrange identical to the existing NotHandled test ...

        // Act
        var result = await dispatcher.DispatchAsync(/* args identical to that test */);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.NotHandled);
        _mockFilterPipeline.Verify(
            f => f.ExecuteOnConsumedSuccessfullyFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
```

- [ ] **Step 2: Run test**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~DispatchAsync_WhenNotHandled_DoesNotInvokeOnConsumedSuccessfullyFilters"
```

Expected: PASS (the dispatcher wiring gates on `!result.NotHandled`).

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.UnitTests/MessageDispatcherTests.cs
git commit -m "test(dispatcher): on-success filters skipped when result.NotHandled"
```

---

### Task 7: Test — throw from an on-success filter produces `Success=false`

**Files:**
- Modify: `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs`

- [ ] **Step 1: Write the test**

Add to `MessageDispatcherTests`:

```csharp
    [Fact]
    public async Task DispatchAsync_OnSuccessFilterThrows_PropagatesAsFailure()
    {
        // Arrange — handler succeeds, but the on-success filter throws.
        var thrown = new InvalidOperationException("on-success boom");

        _mockFilterPipeline
            .Setup(f => f.ExecuteOnConsumedSuccessfullyFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(thrown);

        // ... rest of arrange identical to a happy-path successful-dispatch test ...

        // Act
        var result = await dispatcher.DispatchAsync(/* args */);

        // Assert
        Assert.False(result.Success);
        Assert.Same(thrown, result.Exception);
        // Existing finally-block behavior unchanged: AfterConsumingFilters still runs.
        _mockFilterPipeline.Verify(
            f => f.ExecuteAfterConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
```

- [ ] **Step 2: Run test**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~DispatchAsync_OnSuccessFilterThrows_PropagatesAsFailure"
```

Expected: PASS (the exception falls into the existing `catch (Exception ex)` block which sets `Success = false, Exception = ex`).

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.UnitTests/MessageDispatcherTests.cs
git commit -m "test(dispatcher): on-success filter throw propagates as dispatch failure"
```

---

### Task 8: End-of-group sanity build + grep

**Files:**
- (none modified — verification only)

- [ ] **Step 1: Per-csproj build of every project that depends on the changed surface**

```bash
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj
dotnet build src/ServiceConnect/ServiceConnect.csproj
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj
dotnet build src/ServiceConnect.HealthChecks/ServiceConnect.HealthChecks.csproj
dotnet build src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj
dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj
dotnet build src/ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj
dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
```

Expected: all succeed. (The dedup project still exists; we delete it in Task 9.)

- [ ] **Step 2: Run all FilterPipeline + builder + dispatcher tests filtered**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
  --filter "FullyQualifiedName~FilterPipeline|FullyQualifiedName~ServiceConnectBuilder|FullyQualifiedName~MessageDispatcher"
```

Expected: all pass, no skips.

---

## Group B — Delete the dedup package

### Task 9: Remove the dedup project from `ServiceConnect.slnx`

**Files:**
- Modify: `src/ServiceConnect.slnx`

- [ ] **Step 1: Remove the `<Folder Name="/Filters/">` block**

Open `src/ServiceConnect.slnx`. Delete lines 8–10 (the entire `<Folder Name="/Filters/">` block including its single `<Project>` entry):

```xml
  <Folder Name="/Filters/">
    <Project Path="ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.csproj" />
  </Folder>
```

- [ ] **Step 2: Verify the slnx remaining structure builds**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj
```

Expected: succeeds. (The dedup project's csproj is still on disk; only the slnx reference is removed at this step. Build of the core project does not depend on it.)

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.slnx
git commit -m "build: remove ServiceConnect.Filters.MessageDeduplication from slnx"
```

---

### Task 10: Delete the dedup project + its tests

**Files:**
- Delete: `src/ServiceConnect.Filters.MessageDeduplication/` (entire directory)
- Delete: `src/ServiceConnect.UnitTests/Filters/MessageDeduplication/` (entire directory)

- [ ] **Step 1: Delete the dedup project directory**

```bash
git rm -r src/ServiceConnect.Filters.MessageDeduplication/
```

This removes the project source, csproj, and any tracked `obj`/`bin`. Untracked `obj`/`bin` should be cleaned manually if present:

```bash
rm -rf src/ServiceConnect.Filters.MessageDeduplication/
```

- [ ] **Step 2: Delete the dedup test directory**

```bash
git rm -r src/ServiceConnect.UnitTests/Filters/MessageDeduplication/
```

- [ ] **Step 3: Build the test project to verify no dangling references**

```bash
dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
```

Expected: succeeds. If the test csproj still references the deleted dedup project (look for `<ProjectReference>` to `ServiceConnect.Filters.MessageDeduplication`), edit `src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj` and remove the reference. Re-run the build.

- [ ] **Step 4: Commit**

```bash
git add -A src/ServiceConnect.Filters.MessageDeduplication \
           src/ServiceConnect.UnitTests/Filters/MessageDeduplication \
           src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
git commit -m "feat: remove ServiceConnect.Filters.MessageDeduplication package"
```

---

### Task 11: Repo-wide grep validation + per-csproj build of every remaining project

**Files:**
- (none modified — verification only)

- [ ] **Step 1: Grep for any live references to removed identifiers**

```bash
grep -rln \
  -e "MessageDeduplication" \
  -e "IncomingDeduplicationFilter" \
  -e "OutgoingDeduplicationFilter" \
  -e "IMessageDeduplicationPersistor" \
  -e "DeduplicationFilterSettings" \
  -e "DeduplicationCleanupHostedService" \
  -e "ProcessedMessage" \
  --include="*.cs" --include="*.csproj" --include="*.slnx" --include="*.sln" --include="*.mdx" --include="*.md" \
  src/ examples/ website/ README.md \
  2>/dev/null | grep -v "^examples/MessageDeduplication" | grep -v "^website/src/content/docs/reference/filters/messagededuplication" | grep -v "^website/src/content/docs/learn/operations/idempotency" | grep -v "^website/src/content/docs/samples" | grep -v "^website/astro.config" | grep -v "^website/dist/" | grep -v "^docs/superpowers/"
```

Expected: no output. Any output is a leak — fix and re-run.

(The grep allow-list intentionally excludes the four directories/files we will modify in Group D — `examples/MessageDeduplication/`, the website dedup page, `idempotency.mdx`, `samples.mdx`, `astro.config.mjs`, the build artifacts in `website/dist/`, and the planning docs themselves. Group D removes those references; Task 19/Group D's own grep re-validates with no allow-list.)

- [ ] **Step 2: Per-csproj build of every project under `src/`**

```bash
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj
dotnet build src/ServiceConnect/ServiceConnect.csproj
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj
dotnet build src/ServiceConnect.HealthChecks/ServiceConnect.HealthChecks.csproj
dotnet build src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj
dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj
dotnet build src/ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj
dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
dotnet build src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj
```

Expected: all succeed.

---

## Group C — Sample project

### Task 12: Delete the old sample

**Files:**
- Delete: `examples/MessageDeduplication/` (entire directory)

- [ ] **Step 1: Delete the directory**

```bash
git rm -r examples/MessageDeduplication/
```

- [ ] **Step 2: Commit**

```bash
git commit -m "build: remove examples/MessageDeduplication"
```

---

### Task 13: Scaffold `examples/CustomFilterAndMiddleware/` skeleton

**Files:**
- Create: `examples/CustomFilterAndMiddleware/CustomFilterAndMiddleware.sln`
- Create: `examples/CustomFilterAndMiddleware/run.sh`
- Create: `examples/CustomFilterAndMiddleware/run.ps1`

- [ ] **Step 1: Create the directory layout**

```bash
mkdir -p examples/CustomFilterAndMiddleware/src
```

- [ ] **Step 2: Create the empty solution file**

```bash
cd examples/CustomFilterAndMiddleware && dotnet new sln --name CustomFilterAndMiddleware && cd -
```

- [ ] **Step 3: Copy and adapt the run scripts from the deleted sample's git history**

Recover the previous run scripts from git history for reference:

```bash
git show HEAD~1:examples/MessageDeduplication/run.sh > /tmp/run.sh.previous
git show HEAD~1:examples/MessageDeduplication/run.ps1 > /tmp/run.ps1.previous
```

The new run scripts should:
- Start RabbitMQ in Docker (no Mongo this time).
- Build sender + consumer per-csproj (NOT whole-solution).
- Run the consumer in the background, run the sender, sleep, kill the consumer, print logs.

Adapt /tmp/run.sh.previous to remove Mongo-related steps (look for `mongo`, `27017`, `Mongo` strings) and update project paths to `ServiceConnect.Examples.CustomFilterAndMiddleware.{Sender,Consumer}`. Save to `examples/CustomFilterAndMiddleware/run.sh` and chmod +x. Do the same for run.ps1 (no chmod needed).

- [ ] **Step 4: Commit**

```bash
git add examples/CustomFilterAndMiddleware/
git commit -m "build: scaffold examples/CustomFilterAndMiddleware solution"
```

---

### Task 14: Create the Contracts project

**Files:**
- Create: `examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts.csproj`
- Create: `examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts/OrderPlaced.cs`

- [ ] **Step 1: Create the csproj**

```bash
mkdir -p examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts
cd examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts
dotnet new classlib --framework net10.0 --name ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts --force
rm Class1.cs
cd -
```

Inspect `examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts.csproj` and add a `<ProjectReference Include="../../../../src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj" />` (relative path adjusted for nested depth — verify with `ls`).

- [ ] **Step 2: Create the OrderPlaced message**

`examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts/OrderPlaced.cs`:

```csharp
using ServiceConnect.Interfaces.Messages;

namespace ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts;

public sealed class OrderPlaced : Message
{
    public string OrderId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}
```

(Inspect `src/ServiceConnect.Interfaces/Messages/Message.cs` to confirm the base class and namespace before committing — if `Message` lives in a different namespace, adjust.)

- [ ] **Step 3: Add the project to the sample sln**

```bash
cd examples/CustomFilterAndMiddleware
dotnet sln add src/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts.csproj
cd -
```

- [ ] **Step 4: Build the Contracts project**

```bash
dotnet build examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts.csproj
```

Expected: succeeds.

- [ ] **Step 5: Commit**

```bash
git add examples/CustomFilterAndMiddleware/CustomFilterAndMiddleware.sln \
        examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts/
git commit -m "feat(example): add CustomFilterAndMiddleware Contracts project"
```

---

### Task 15: Create the Consumer project skeleton

**Files:**
- Create: `examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.csproj`
- Create: `examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/OrderPlacedHandler.cs`

- [ ] **Step 1: Create the csproj**

```bash
mkdir -p examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Filters
mkdir -p examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Middleware
cd examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer
dotnet new console --framework net10.0 --name ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer --force
cd -
```

Edit `examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.csproj` and add the project references (relative paths adjusted for nested depth — verify with `ls`):

```xml
  <ItemGroup>
    <ProjectReference Include="../ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts.csproj" />
    <ProjectReference Include="../../../../src/ServiceConnect/ServiceConnect.csproj" />
    <ProjectReference Include="../../../../src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj" />
  </ItemGroup>
```

Add the standard `Microsoft.Extensions.Hosting` package reference if needed (inspect a sibling example like `examples/PointToPoint/` for the canonical hosting setup).

- [ ] **Step 2: Create the handler**

`examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/OrderPlacedHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts;
using ServiceConnect.Interfaces.Handlers;

namespace ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer;

public sealed class OrderPlacedHandler(ILogger<OrderPlacedHandler> logger) : IMessageHandler<OrderPlaced>
{
    private static int _attemptsForCrashOrder;

    public IConsumeContext Context { get; set; } = null!;

    public Task HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default)
    {
        // Demonstrate scenario 2 (handler-crash → broker redelivery → dedup filter does NOT block).
        // The first time we see "crash-once", throw — the broker redelivers and the second attempt succeeds.
        if (message.OrderId == "crash-once" && Interlocked.Exchange(ref _attemptsForCrashOrder, 1) == 0)
        {
            logger.LogWarning("Throwing on first delivery of crash-once to demonstrate redelivery handling");
            throw new InvalidOperationException("simulated handler crash");
        }

        logger.LogInformation("Handled OrderPlaced {OrderId} (amount {Amount:C})", message.OrderId, message.Amount);
        return Task.CompletedTask;
    }
}
```

(Inspect `src/ServiceConnect.Interfaces/Handlers/IMessageHandler.cs` and `IConsumeContext.cs` to confirm namespace/shape.)

- [ ] **Step 3: Add to sln + build**

```bash
cd examples/CustomFilterAndMiddleware
dotnet sln add src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.csproj
cd -
dotnet build examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.csproj
```

Expected: build succeeds. (Program.cs is still the dotnet-new-template default — that's fine, it'll be replaced in Task 19.)

- [ ] **Step 4: Commit**

```bash
git add examples/CustomFilterAndMiddleware/CustomFilterAndMiddleware.sln \
        examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/
git commit -m "feat(example): add CustomFilterAndMiddleware Consumer skeleton"
```

---

### Task 16: Create `IDedupePersistor` and `InMemoryDedupePersistor`

**Files:**
- Create: `examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Filters/IDedupePersistor.cs`
- Create: `examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Filters/InMemoryDedupePersistor.cs`

- [ ] **Step 1: Create `IDedupePersistor`**

`examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Filters/IDedupePersistor.cs`:

```csharp
namespace ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.Filters;

/// <summary>
/// Sample dedupe persistor contract. The atomic <see cref="TryInsertAsync"/>
/// returns true on first insert, false on duplicate — letting the on-success
/// filter make the consume-side dedup decision in a single round trip.
/// </summary>
public interface IDedupePersistor
{
    Task<bool> ContainsAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomic insert. Returns true if the id was new, false if it was already present.
    /// </summary>
    Task<bool> TryInsertAsync(Guid messageId, DateTime expiry, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Create `InMemoryDedupePersistor`**

`examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Filters/InMemoryDedupePersistor.cs`:

```csharp
using System.Collections.Concurrent;

namespace ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.Filters;

/// <summary>
/// Per-process dedupe persistor. Suitable for the sample only — does not
/// survive process restart and does not coordinate across replicas.
/// For production, see the README's "scaling out" appendix.
/// </summary>
public sealed class InMemoryDedupePersistor : IDedupePersistor
{
    private readonly ConcurrentDictionary<Guid, DateTime> _seen = new();

    public Task<bool> ContainsAsync(Guid messageId, CancellationToken cancellationToken = default)
        => Task.FromResult(_seen.ContainsKey(messageId));

    public Task<bool> TryInsertAsync(Guid messageId, DateTime expiry, CancellationToken cancellationToken = default)
        => Task.FromResult(_seen.TryAdd(messageId, expiry));
}
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.csproj
```

Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Filters/
git commit -m "feat(example): add IDedupePersistor + InMemoryDedupePersistor"
```

---

### Task 17: Create `DedupeIncomingFilter` and `DedupeOnSuccessFilter`

**Files:**
- Create: `examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Filters/DedupeIncomingFilter.cs`
- Create: `examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Filters/DedupeOnSuccessFilter.cs`

- [ ] **Step 1: Create the BeforeConsuming filter**

`examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Filters/DedupeIncomingFilter.cs`:

```csharp
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.Filters;

/// <summary>
/// BeforeConsuming filter. Consults the persistor; if the MessageId is already
/// recorded, returns Stop so the dispatcher acks-and-drops the redelivery
/// without invoking the handler.
/// </summary>
public sealed class DedupeIncomingFilter(
    IDedupePersistor persistor,
    ILogger<DedupeIncomingFilter> logger) : IFilter
{
    public async Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        if (!envelope.Headers.TryGetValue("MessageId", out var raw) ||
            !Guid.TryParse(HeaderDecoder.Decode(raw), out var messageId))
        {
            // No id, can't dedupe — let the message through.
            return FilterAction.Continue;
        }

        if (await persistor.ContainsAsync(messageId, cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation("Dedupe: blocking duplicate delivery of MessageId {MessageId}", messageId);
            return FilterAction.Stop;
        }

        return FilterAction.Continue;
    }
}
```

- [ ] **Step 2: Create the OnConsumedSuccessfully filter**

`examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Filters/DedupeOnSuccessFilter.cs`:

```csharp
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.Filters;

/// <summary>
/// OnConsumedSuccessfully filter. Only fires after the handler has completed
/// successfully — failures and unhandled messages skip this stage. Records the
/// MessageId atomically; if the persistor reports the id was already present
/// (e.g. two concurrent deliveries raced past the BeforeConsuming check), the
/// filter throws so the dispatcher returns Success=false and the broker
/// redelivers, letting the next attempt's BeforeConsuming filter block.
/// </summary>
public sealed class DedupeOnSuccessFilter(
    IDedupePersistor persistor,
    ILogger<DedupeOnSuccessFilter> logger) : IFilter
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public async Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        if (!envelope.Headers.TryGetValue("MessageId", out var raw) ||
            !Guid.TryParse(HeaderDecoder.Decode(raw), out var messageId))
        {
            return FilterAction.Continue;
        }

        var inserted = await persistor.TryInsertAsync(messageId, DateTime.UtcNow + Retention, cancellationToken)
            .ConfigureAwait(false);

        if (!inserted)
        {
            // BeforeConsuming and OnSuccess raced. Throwing here makes the dispatcher
            // return Success=false → broker redelivers → next attempt's BeforeConsuming
            // filter sees the id and returns Stop.
            throw new InvalidOperationException(
                $"Concurrent delivery of MessageId {messageId} already recorded; redelivery will dedupe.");
        }

        logger.LogDebug("Dedupe: recorded MessageId {MessageId}", messageId);
        return FilterAction.Continue;
    }
}
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.csproj
```

Expected: succeeds. If `HeaderDecoder` is in a different namespace than `ServiceConnect.Interfaces`, inspect `src/ServiceConnect.Interfaces/Headers/HeaderDecoder.cs` and adjust the using.

- [ ] **Step 4: Commit**

```bash
git add examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Filters/
git commit -m "feat(example): add DedupeIncomingFilter + DedupeOnSuccessFilter"
```

---

### Task 18: Create `LoggingTimingMiddleware`

**Files:**
- Create: `examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Middleware/LoggingTimingMiddleware.cs`

- [ ] **Step 1: Inspect the IMessageProcessingMiddleware shape**

```bash
cat src/ServiceConnect.Interfaces/Pipelines/IMessageProcessingMiddleware.cs
```

Note the exact method signature and namespace before writing.

- [ ] **Step 2: Create the middleware**

`examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Middleware/LoggingTimingMiddleware.cs`:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
// Adjust the namespace below after inspecting IMessageProcessingMiddleware.cs in step 1.
// IMessageProcessingMiddleware lives in ServiceConnect.Interfaces — the exact sub-namespace
// depends on the file.

namespace ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.Middleware;

/// <summary>
/// Middleware demonstration. Wraps every handler invocation with structured
/// log entries and a timing measurement. Unlike a filter, middleware runs
/// inline around the handler and observes the message bytes / type / instance.
/// </summary>
public sealed class LoggingTimingMiddleware(ILogger<LoggingTimingMiddleware> logger) : IMessageProcessingMiddleware
{
    public async Task<ConsumeEventResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes,
        Type messageType,
        object message,
        IDictionary<string, object> headers,
        Envelope envelope,
        MessageProcessingDelegate next,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation("→ Handler entry for {MessageType}", messageType.Name);
        try
        {
            var result = await next(messageBytes, messageType, message, headers, envelope, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            logger.LogInformation("← Handler exit for {MessageType} in {ElapsedMs}ms (Success={Success})",
                messageType.Name, sw.ElapsedMilliseconds, result.Success);
            return result;
        }
        catch
        {
            sw.Stop();
            logger.LogInformation("← Handler exit for {MessageType} in {ElapsedMs}ms (THREW)",
                messageType.Name, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
```

The `MessageProcessingDelegate` and `IMessageProcessingMiddleware` signatures may differ slightly from the above sketch — adjust to match the file you read in step 1 verbatim.

- [ ] **Step 3: Build**

```bash
dotnet build examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.csproj
```

Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Middleware/
git commit -m "feat(example): add LoggingTimingMiddleware"
```

---

### Task 19: Wire Consumer/Program.cs

**Files:**
- Modify: `examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Program.cs`

- [ ] **Step 1: Inspect a sibling sample's Program.cs for the canonical Generic Host setup**

```bash
cat examples/PointToPoint/src/*.Consumer*/Program.cs 2>/dev/null | head -80
```

(Or any other sibling Consumer; pick whichever is most idiomatic.)

- [ ] **Step 2: Replace `Program.cs` with the wire-up**

`examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer;
using ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.Filters;
using ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.Middleware;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(o => o.SingleLine = true);
    })
    .ConfigureServices(services =>
    {
        services.AddSingleton<IDedupePersistor, InMemoryDedupePersistor>();
        services.AddTransient<DedupeIncomingFilter>();
        services.AddTransient<DedupeOnSuccessFilter>();
        services.AddTransient<LoggingTimingMiddleware>();
        services.AddTransient<OrderPlacedHandler>();

        services.AddServiceConnect(builder =>
        {
            builder.ConfigureQueues(queues =>
            {
                queues.QueueName = "custom-filter-and-middleware-sample";
            });
            builder.UseRabbitMQ(transport =>
            {
                transport.Host = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";
            });
            builder.AddBeforeConsumingFilter<DedupeIncomingFilter>();
            builder.AddOnConsumedSuccessfullyFilter<DedupeOnSuccessFilter>();
            builder.AddMessageProcessingMiddleware<LoggingTimingMiddleware>();
        });
    })
    .Build();

await host.RunAsync();
```

The exact builder API (e.g. `ConfigureQueues`, `UseRabbitMQ`) should match the sibling sample inspected in step 1 — this is a sketch. The three filter/middleware lines are load-bearing and must be exactly as shown.

- [ ] **Step 3: Build**

```bash
dotnet build examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.csproj
```

Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/Program.cs
git commit -m "feat(example): wire Consumer Program.cs (filters + middleware + handler)"
```

---

### Task 20: Create the Sender project

**Files:**
- Create: `examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender.csproj`
- Create: `examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender/Program.cs`

- [ ] **Step 1: Create the csproj**

```bash
mkdir -p examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender
cd examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender
dotnet new console --framework net10.0 --name ServiceConnect.Examples.CustomFilterAndMiddleware.Sender --force
cd -
```

Edit the csproj and add references (paths adjusted for nested depth — verify with `ls`):

```xml
  <ItemGroup>
    <ProjectReference Include="../ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts.csproj" />
    <ProjectReference Include="../../../../src/ServiceConnect/ServiceConnect.csproj" />
    <ProjectReference Include="../../../../src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj" />
  </ItemGroup>
```

Add `Microsoft.Extensions.Hosting` package reference if not picked up transitively.

- [ ] **Step 2: Replace `Program.cs`**

Inspect a sibling `Sender/Program.cs` for the canonical send pattern (e.g. `examples/PointToPoint/`), then:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts;
using ServiceConnect.Interfaces;

using var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddServiceConnect(builder =>
        {
            builder.ConfigureQueues(queues =>
            {
                // Sender does not consume; queue name is irrelevant here.
                queues.QueueName = "custom-filter-and-middleware-sender";
            });
            builder.UseRabbitMQ(transport =>
            {
                transport.Host = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";
            });
        });
    })
    .Build();

await host.StartAsync();

var bus = host.Services.GetRequiredService<IBus>();

// Scenario 1: a normal message — handler runs, on-success filter records.
await bus.Send("custom-filter-and-middleware-sample",
    new OrderPlaced { OrderId = "order-1", Amount = 42.50m });

// Scenario 2: handler crashes once, broker redelivers, second attempt succeeds.
// On-success does NOT record on the failed first attempt; redelivery proceeds.
await bus.Send("custom-filter-and-middleware-sample",
    new OrderPlaced { OrderId = "crash-once", Amount = 99.00m });

// One more normal message so the consumer log shows the timing middleware repeatedly.
await bus.Send("custom-filter-and-middleware-sample",
    new OrderPlaced { OrderId = "order-2", Amount = 7.00m });

Console.WriteLine("Sender: published three messages, exiting.");
await host.StopAsync();
```

(The `IBus.Send(string endpoint, T message)` signature should match the existing surface; verify against `src/ServiceConnect.Interfaces/Bus/IBus.cs`.)

- [ ] **Step 3: Add to sln + build**

```bash
cd examples/CustomFilterAndMiddleware
dotnet sln add src/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender.csproj
cd -
dotnet build examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender.csproj
```

Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender/ \
        examples/CustomFilterAndMiddleware/CustomFilterAndMiddleware.sln
git commit -m "feat(example): add CustomFilterAndMiddleware Sender project"
```

---

### Task 21: Write the sample README

**Files:**
- Create: `examples/CustomFilterAndMiddleware/README.md`

- [ ] **Step 1: Write the README**

`examples/CustomFilterAndMiddleware/README.md`:

```markdown
# CustomFilterAndMiddleware sample

Demonstrates the two ServiceConnect extension points that let you customise
the consume pipeline: **filters** (envelope-level pre/post hooks) and
**message-processing middleware** (handler-wrapping middleware that observes
the deserialised message).

The worked scenario is broker-redelivery deduplication — the canonical use
case for the on-success filter stage. Implementing dedupe correctly requires
two filters, not one:

- A `BeforeConsumingFilter` that consults a persistor and short-circuits when
  the `MessageId` is already recorded.
- An `OnConsumedSuccessfullyFilter` that records the `MessageId` **only** if
  the handler completed successfully. Recording before the handler runs (or
  in an `AfterConsumingFilter`, which runs on both success and failure paths)
  silently drops legitimate broker redeliveries after a handler crash.

## How to run

Requires Docker + .NET 10 SDK.

```bash
./run.sh
```

This starts RabbitMQ in a container, builds the sender + consumer, runs the
consumer in the background, runs the sender, sleeps a few seconds, kills the
consumer, and prints the consumer log.

## What you should see

The sender publishes three messages: `order-1`, `crash-once`, `order-2`.
The consumer's log shows:

- `LoggingTimingMiddleware` printing `→` and `←` markers around each handler
  invocation with elapsed time.
- `crash-once` is delivered twice: the first attempt throws (the middleware
  prints `THREW`), the broker redelivers, the second attempt succeeds. The
  on-success filter records the id only on the second attempt.
- The `DedupeIncomingFilter` logs nothing because none of the three sender
  messages is a redelivery of an *already-recorded* id (the handler's first
  attempt at `crash-once` failed, so the id was not recorded — the redelivery
  proceeds).

To see the BeforeConsuming filter actually block a duplicate, manually run
`./run.sh` twice without restarting the consumer container — the second run's
sender will publish messages whose ids have already been recorded by the
first run's consumer (subject to consumer process restart caveats; see below).

## Filter walkthrough

`Filters/IDedupePersistor.cs` — the contract the sample's two filters share.
The atomic `TryInsertAsync` returns true if the id was new, false if a
concurrent caller already recorded it. This atomicity is the whole point: a
read-then-write pattern (`ContainsAsync` then `InsertAsync`) admits two
concurrent deliveries past the existence check before either records,
defeating dedupe under contention.

`Filters/InMemoryDedupePersistor.cs` — a per-process implementation backed by
`ConcurrentDictionary.TryAdd`. Suitable for the sample only; does not survive
process restart and does not coordinate across replicas.

`Filters/DedupeIncomingFilter.cs` — `BeforeConsuming` filter. Reads the
`MessageId` header, consults the persistor, returns `Stop` if present.

`Filters/DedupeOnSuccessFilter.cs` — `OnConsumedSuccessfully` filter. Records
the id atomically. If the persistor reports the id was already present (a
race past the BeforeConsuming check), throws — the dispatcher returns
`Success=false` so the broker redelivers and the next attempt's
BeforeConsuming filter blocks.

## Middleware walkthrough

`Middleware/LoggingTimingMiddleware.cs` — wraps every handler invocation with
entry/exit log lines and a `Stopwatch`. Middleware differs from filters in
two ways:

1. Middleware is **inside** the dispatch — it sees the deserialised message
   instance, not just the envelope.
2. Middleware uses `next(...)` to invoke the next stage explicitly, allowing
   pre- and post-handler logic in a single class. Filters short-circuit by
   returning `FilterAction.Stop`; middleware short-circuits by not calling
   `next`.

Reach for middleware when you want to wrap the handler call with timing,
tracing, or transactional scoping. Reach for a filter when you want to make
an admission decision based on the envelope or its headers.

## Production caveats

The toy `InMemoryDedupePersistor` is **not** production-ready:

- It does not survive process restart. A consumer pod that's killed mid-flight
  loses every recorded id.
- It does not coordinate across consumer replicas. Two consumers behind the
  same queue will each maintain their own dictionary.
- It has no expiry/eviction. Memory grows without bound.

For real workloads, implement `IDedupePersistor` against a shared store with
an atomic insert primitive. Sketch for MongoDB:

```csharp
public sealed class MongoDedupePersistor(IMongoCollection<ProcessedMessage> col) : IDedupePersistor
{
    public async Task<bool> ContainsAsync(Guid id, CancellationToken ct = default)
        => await col.Find(p => p.Id == id).AnyAsync(ct);

    public async Task<bool> TryInsertAsync(Guid id, DateTime expiry, CancellationToken ct = default)
    {
        try
        {
            await col.InsertOneAsync(new ProcessedMessage { Id = id, Expiry = expiry }, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }
}
```

Pair this with a unique index on `_id` and a TTL index on `Expiry`. The TTL
index handles cleanup; no separate cleanup hosted service needed.

Handler-side idempotency remains the canonical answer where it's available
(idempotent business operations, natural upsert keys). The filter pattern
above is a belt-and-braces layer for handlers whose side effects can't be
made idempotent at the business level.

See also: [Idempotency](https://github.com/R-Suite/ServiceConnect-CSharp/blob/master/website/src/content/docs/learn/operations/idempotency.mdx).
```

- [ ] **Step 2: Commit**

```bash
git add examples/CustomFilterAndMiddleware/README.md
git commit -m "docs(example): add CustomFilterAndMiddleware README with filter/middleware walkthrough"
```

---

### Task 22: Sample build + smoke test

**Files:**
- (none modified — verification)

- [ ] **Step 1: Build all three sample csprojs**

```bash
dotnet build examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts.csproj
dotnet build examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.csproj
dotnet build examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender.csproj
```

Expected: all succeed.

- [ ] **Step 2: Smoke-run via `run.sh`**

```bash
cd examples/CustomFilterAndMiddleware
./run.sh
```

Expected output (key markers — exact wording may differ):

- Sender prints "published three messages, exiting".
- Consumer log shows `→ Handler entry for OrderPlaced` three times for the successful deliveries plus one `THREW` line for the first attempt at `crash-once`.
- No exceptions escape the consumer process.

If anything is off, fix in place and amend (or add a follow-up commit). If the smoke test surfaces a real bug in the new pipeline stage (e.g. on-success not firing for a successful delivery), regress against Task 4–7's tests; one of them is wrong.

- [ ] **Step 3: Commit any final fixes from the smoke test**

```bash
git add -A examples/CustomFilterAndMiddleware/
git commit -m "fix(example): adjustments from smoke test" || true
```

(Only commits if there were fixes.)

---

## Group D — Documentation + website

### Task 23: Delete the website's dedup reference page

**Files:**
- Delete: `website/src/content/docs/reference/filters/messagededuplication.mdx`

- [ ] **Step 1: Delete + commit**

```bash
git rm website/src/content/docs/reference/filters/messagededuplication.mdx
git commit -m "docs(website): remove dedup-package reference page"
```

---

### Task 24: Update `website/astro.config.mjs` sidebar

**Files:**
- Modify: `website/astro.config.mjs`

- [ ] **Step 1: Remove the dedup sidebar entry**

Open `website/astro.config.mjs` and delete line 127:

```js
{ label: 'Message Deduplication', link: '/reference/filters/messagededuplication/' },
```

Leave the surrounding `Filters & Middleware` group otherwise unchanged.

- [ ] **Step 2: Commit**

```bash
git add website/astro.config.mjs
git commit -m "docs(website): remove dedup sidebar entry"
```

---

### Task 25: Update `ifilter.mdx` — add 4th stage + fix worked example

**Files:**
- Modify: `website/src/content/docs/reference/filters/ifilter.mdx`

- [ ] **Step 1: Add the 4th pipeline-stage entry**

Read the file. The current pipeline-stage section (around lines 40–92) enumerates `ExecuteOutgoingFiltersAsync`, `ExecuteBeforeConsumingFiltersAsync`, `ExecuteAfterConsumingFiltersAsync`. Add a fourth subsection mirroring the same shape:

```markdown
### `ExecuteOnConsumedSuccessfullyFiltersAsync`

```csharp
Task<FilterAction> ExecuteOnConsumedSuccessfullyFiltersAsync(
    Envelope envelope,
    CancellationToken cancellationToken = default);
```

Runs only after a successful handler invocation — the dispatcher's chain
returned `Success = true` and `NotHandled = false`. Failures and unhandled
messages skip this stage.

A filter throwing here propagates as a dispatch failure: the dispatcher's
`catch` block sets `result.Success = false` and the broker redelivers. Use
this to record at-most-once side effects (deduplication keys, audit events,
outbox rows) that depend on the handler having actually completed.

`FilterAction.Stop` halts further on-success filters but does **not** flip
`result.Success` to false — consumption already succeeded.
```

(Match the formatting and admonition style of the existing three stages.)

- [ ] **Step 2: Update the `DuplicateDetectionFilter` worked example**

The current example (around lines 100–160) records the dedup id from a single `BeforeConsuming` filter — exactly the C2 anti-pattern. Replace with a two-filter pattern that uses the new on-success stage. The shape should mirror the sample at `examples/CustomFilterAndMiddleware/`:

- A `BeforeConsuming` filter that *only* checks existence (`Stop` if present).
- An `OnConsumedSuccessfully` filter that *only* records (atomic insert).

The registration block changes from:

```csharp
builder.AddBeforeConsumingFilter<DuplicateDetectionFilter>();
```

to:

```csharp
builder.AddBeforeConsumingFilter<DuplicateCheckFilter>();
builder.AddOnConsumedSuccessfullyFilter<DuplicateRecordFilter>();
```

The narrative paragraph that follows the code (currently line 160) should be updated to explain *why* two filters: the prose `DuplicateDetectionFilter runs as a before-consuming stage in front of every handler...` becomes `The pair of filters runs in two stages: the before-consuming stage short-circuits the dispatch when the MessageId is already recorded; the on-success stage records the id atomically only after the handler has completed. Recording in the before-consuming stage (or in after-consuming, which runs on both success and failure) silently drops legitimate broker redeliveries after a handler crash.`

- [ ] **Step 3: Build the website to verify**

```bash
npm --prefix website install
npm --prefix website run build
```

Expected: `Astro` build succeeds, no broken links.

- [ ] **Step 4: Commit**

```bash
git add website/src/content/docs/reference/filters/ifilter.mdx
git commit -m "docs(website): document OnConsumedSuccessfully filter stage; fix worked example"
```

---

### Task 26: Update `ipipelineconfiguration.mdx`

**Files:**
- Modify: `website/src/content/docs/reference/configuration/ipipelineconfiguration.mdx`

- [ ] **Step 1: Add `OnConsumedSuccessfullyFilters` to the property list**

Inspect the existing file — it should enumerate `BeforeConsumingFilters`, `AfterConsumingFilters`, `OutgoingFilters`, `MessageProcessingMiddleware`, `SendMessageMiddleware`. Add a fourth filter entry between `AfterConsumingFilters` and `OutgoingFilters` (or wherever the existing flow places it):

```markdown
### `OnConsumedSuccessfullyFilters`

The filters that run only after a successful handler invocation. The
dispatcher invokes this stage when the chain returned
`ConsumeEventResult.Success = true` and `NotHandled = false` — failures and
unhandled messages skip it. Use for at-most-once side effects (dedup,
outbox, audit) that depend on the handler having completed.

See [`IFilter.ExecuteOnConsumedSuccessfullyFiltersAsync`](../../filters/ifilter/#executeonconsumedsuccessfullyfiltersasync) for filter-author semantics.
```

- [ ] **Step 2: Build the website**

```bash
npm --prefix website run build
```

- [ ] **Step 3: Commit**

```bash
git add website/src/content/docs/reference/configuration/ipipelineconfiguration.mdx
git commit -m "docs(website): document OnConsumedSuccessfullyFilters in IPipelineConfiguration"
```

---

### Task 27: Update `idempotency.mdx`

**Files:**
- Modify: `website/src/content/docs/learn/operations/idempotency.mdx`

- [ ] **Step 1: Read the existing file**

```bash
cat website/src/content/docs/learn/operations/idempotency.mdx
```

- [ ] **Step 2: Rewrite the "infrastructure-level belt-and-braces" section**

The current page links to the deleted `reference/filters/messagededuplication/` page. Restructure as:

1. **Lead with handler-side idempotency.** Idempotent operations and natural upsert keys are the canonical answer.
2. **Filter-pattern alternative.** When handler-side idempotency isn't possible, build a per-consumer dedup filter pair using `BeforeConsuming` + the new `OnConsumedSuccessfully` stage. Explain the asymmetry: one short-circuits before the handler, one records only after.
3. **Trade-offs.** TOCTOU under contention (use atomic insert primitives — unique indexes + duplicate-key detection in stores that support them). Cross-process persistor: in-process dedup only deduplicates within a single replica. Broker-redelivery vs duplicate-publish: these are different problems requiring different solutions.
4. **Worked example pointer.** Link to the new sample at `examples/CustomFilterAndMiddleware/` and to the [`IFilter` reference](../../reference/filters/ifilter/).

Replace any link to `reference/filters/messagededuplication/` with `reference/filters/ifilter/` or a sample link as appropriate.

- [ ] **Step 3: Build + commit**

```bash
npm --prefix website run build
git add website/src/content/docs/learn/operations/idempotency.mdx
git commit -m "docs(website): rewrite idempotency page for handler-side + filter-pattern guidance"
```

---

### Task 28: Update `samples.mdx`

**Files:**
- Modify: `website/src/content/docs/samples.mdx`

- [ ] **Step 1: Replace the `### MessageDeduplication` block (lines 99–106) with the new entry**

Current (verbatim from inspection):

```markdown
### MessageDeduplication

Runnable end-to-end demo of the `ServiceConnect.Filters.MessageDeduplication`...
[Message Deduplication](/ServiceConnect-CSharp/reference/filters/messagededuplication/)
[`examples/MessageDeduplication`](https://github.com/R-Suite/ServiceConnect-CSharp/tree/master/examples/MessageDeduplication)
```

Replace with:

```markdown
### CustomFilterAndMiddleware

Runnable end-to-end demo of how to build your own filter and middleware against the public extension points. The worked scenario is broker-redelivery deduplication using the `OnConsumedSuccessfully` pipeline stage.

[`IFilter`](/ServiceConnect-CSharp/reference/filters/ifilter/) · [`IMessageProcessingMiddleware`](/ServiceConnect-CSharp/reference/filters/imessageprocessingmiddleware/) · [`examples/CustomFilterAndMiddleware`](https://github.com/R-Suite/ServiceConnect-CSharp/tree/master/examples/CustomFilterAndMiddleware)
```

- [ ] **Step 2: Build + commit**

```bash
npm --prefix website run build
git add website/src/content/docs/samples.mdx
git commit -m "docs(website): rename MessageDeduplication sample entry to CustomFilterAndMiddleware"
```

---

### Task 29: Update `releases.mdx` (v7 entry)

**Files:**
- Modify: `website/src/content/docs/releases.mdx`

- [ ] **Step 1: Inspect the existing v7 release entry**

```bash
cat website/src/content/docs/releases.mdx
```

Find the v7 section heading. If a v7 section already exists, add bullets to it; if not, create one in the conventional shape used by the file.

- [ ] **Step 2: Add bullets**

Add (or merge into existing v7 entry):

```markdown
**Breaking — `ServiceConnect.Filters.MessageDeduplication` removed.** The package shipped redelivery dedup in a way that silently dropped legitimate broker redeliveries after a handler crash, recorded keys before the handler ran, and shared in-memory state across instances via a static field. v7 deletes the package outright. Build per-consumer dedupe using the new `OnConsumedSuccessfully` pipeline stage; see the [`CustomFilterAndMiddleware` sample](https://github.com/R-Suite/ServiceConnect-CSharp/tree/master/examples/CustomFilterAndMiddleware) for a worked implementation. Handler-side idempotency remains the canonical answer.

**New — `OnConsumedSuccessfully` filter stage.** A fourth pipeline stage that runs only after a successful handler invocation (`result.Success && !result.NotHandled`). Register filters via `builder.AddOnConsumedSuccessfullyFilter<T>()`. Use for at-most-once side effects that depend on the handler having completed: dedupe-key recording, outbox writes, audit events.
```

- [ ] **Step 3: Build + commit**

```bash
npm --prefix website run build
git add website/src/content/docs/releases.mdx
git commit -m "docs(website): v7 release notes — dedup-package removal + OnConsumedSuccessfully"
```

---

### Task 30: Verify `learn/messaging-patterns/filters.mdx`

**Files:**
- Modify (only if needed): `website/src/content/docs/learn/messaging-patterns/filters.mdx`

- [ ] **Step 1: Inspect the file**

```bash
grep -n "BeforeConsuming\|AfterConsuming\|OnConsumed\|stages\|stage" website/src/content/docs/learn/messaging-patterns/filters.mdx
```

- [ ] **Step 2: Update if it enumerates stages**

If the page lists the pipeline stages (Before/After/Outgoing) anywhere, add `OnConsumedSuccessfully` alongside with one-sentence semantics: *"runs only after a successful handler invocation; failures and unhandled messages skip it"*. If it doesn't enumerate stages, no change needed — note that explicitly in the commit message.

- [ ] **Step 3: Build + commit (only if changed)**

```bash
npm --prefix website run build
git add website/src/content/docs/learn/messaging-patterns/filters.mdx
git commit -m "docs(website): mention OnConsumedSuccessfully stage in filters concept page"
# Or, if no change was needed:
echo "No change needed; page does not enumerate stages."
```

---

### Task 31: Update repo-root README + examples README

**Files:**
- Modify: `README.md` (repo root)
- Modify: `examples/README.md`

- [ ] **Step 1: Grep root README for dedup mentions**

```bash
grep -n "MessageDeduplication\|dedup\|Dedup" README.md
```

- [ ] **Step 2: Update root README**

For each match, decide:
- Remove the line if it's a feature pitch for the deleted package.
- Replace with a pointer to the new sample if it's a "see also" section.

- [ ] **Step 3: Update examples/README.md**

```bash
grep -n "MessageDeduplication" examples/README.md
```

Rename the entry to `CustomFilterAndMiddleware` and refresh the description to: *"How to build a custom filter (using the BeforeConsuming + OnConsumedSuccessfully pipeline stages) and a custom IMessageProcessingMiddleware. Worked scenario: broker-redelivery deduplication."*

- [ ] **Step 4: Commit**

```bash
git add README.md examples/README.md
git commit -m "docs: update READMEs for dedup-package removal and new sample"
```

---

### Task 32: Final verification

**Files:**
- (none modified — verification only)

- [ ] **Step 1: Repo-wide grep with no allow-list**

```bash
grep -rln \
  -e "MessageDeduplication" \
  -e "IncomingDeduplicationFilter" \
  -e "OutgoingDeduplicationFilter" \
  -e "IMessageDeduplicationPersistor" \
  -e "DeduplicationFilterSettings" \
  -e "DeduplicationCleanupHostedService" \
  --include="*.cs" --include="*.csproj" --include="*.slnx" --include="*.sln" --include="*.mdx" --include="*.md" \
  src/ examples/ website/src/ README.md \
  2>/dev/null
```

Expected: empty output. (Allowed: matches in `docs/superpowers/` planning docs and in `website/dist/` build artifacts — these don't violate the constraint.)

- [ ] **Step 2: Per-csproj build of every project**

```bash
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj
dotnet build src/ServiceConnect/ServiceConnect.csproj
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj
dotnet build src/ServiceConnect.HealthChecks/ServiceConnect.HealthChecks.csproj
dotnet build src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj
dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj
dotnet build src/ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj
dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
dotnet build src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj

dotnet build examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts/ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts.csproj
dotnet build examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.csproj
dotnet build examples/CustomFilterAndMiddleware/src/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender.csproj
```

Expected: all succeed.

- [ ] **Step 3: Run the relevant tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
  --filter "FullyQualifiedName~FilterPipeline|FullyQualifiedName~ServiceConnectBuilder|FullyQualifiedName~MessageDispatcher"
```

Expected: all pass.

- [ ] **Step 4: Astro build**

```bash
npm --prefix website run build
```

Expected: succeeds, no broken-link warnings.

- [ ] **Step 5: Code review (optional but recommended)**

Per the phase doc's general guidance, invoke `superpowers:requesting-code-review` against the diff range for this phase before merging. The reviewer should confirm:

- New stage tests cover all five behaviours from the spec (success-fires, throw-skips, NotHandled-skips, Stop-doesn't-flip, throw-from-filter-fails).
- Deletion sweep is clean (grep checks above).
- Sample's filter and middleware code matches the website's worked example exactly (so users following either path land on the same idiom).

---

## Self-review

**Spec coverage check.** Walking through `docs/superpowers/specs/2026-04-29-phase-01-dedup-redesign.md`:

- "Deletions" section — Tasks 9, 10, 12, 23, 24 cover all listed paths.
- "Additions / new pipeline stage" — Tasks 1–4 cover interface + impl + builder + dispatcher.
- "Sample" — Tasks 13–22.
- "Documentation updates" — Tasks 23–31.
- "Pipeline-stage semantics" (skip on `NotHandled`, throw → Success=false, Stop doesn't flip success) — Tasks 4, 5, 6, 7 cover the success/throw/NotHandled cases. The `Stop`-doesn't-flip case is covered by the `FilterPipelineTests.ExecuteOnConsumedSuccessfullyFiltersAsync_WhenFilterStops_PipelineStops` test in Task 2 (verifies the pipeline returns `Stop`) combined with the dispatcher's intentional discard of the return value (visible in Task 4's code change). If a reviewer asks "but where's a dispatcher-level test that asserts `result.Success` stays true even when on-success returns Stop?", add one as a follow-up — the current coverage is structurally sufficient because `Stop` doesn't change the result variable, but an explicit test is cheap.
- "Test strategy: scope-resolution test" — covered by the existing `FilterPipeline` test fixture (the `_pipeline` is constructed with the existing `ConsumeScopeAccessor` push pattern; new-stage tests inherit that resolution behaviour through the parametric `_pipeline` and `_mockServiceProvider`).
- "Verification gate" — Task 32 walks the gate (tests, builds, grep, Astro, optional code review).

**Placeholder scan.** Plan was drafted with explicit code blocks throughout. Two intentional notes:
- Task 4 says "args identical to the existing happy-path test" rather than reproducing the full arrange block. This is because `MessageDispatcherTests.cs` is large (>700 lines) and the harness setup is non-trivial; reproducing it inline would be longer than the rest of the plan combined. The implementing engineer is told exactly which existing test to copy from.
- Tasks 5–7 use the same convention. Same justification.
- Task 18's middleware code has a `// Adjust the namespace` comment because `IMessageProcessingMiddleware`'s exact sub-namespace within `ServiceConnect.Interfaces` wasn't read during planning. Step 1 of that task explicitly directs the engineer to inspect the file before writing.

If those three are problematic, the implementing agent can inline the existing test arrange block as their first step in each task.

**Type / signature consistency.** New surface types reused consistently:
- `ExecuteOnConsumedSuccessfullyFiltersAsync` — same name in interface, implementation, dispatcher call site, and tests.
- `OnConsumedSuccessfullyFilters` — same name in interface, implementation, builder, and tests.
- `AddOnConsumedSuccessfullyFilter<T>()` — same name in builder, sample wire-up, README, website docs.
- `IDedupePersistor` / `InMemoryDedupePersistor` / `DedupeIncomingFilter` / `DedupeOnSuccessFilter` / `LoggingTimingMiddleware` — same names across sample tasks.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-29-phase-01-dedup-redesign.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
