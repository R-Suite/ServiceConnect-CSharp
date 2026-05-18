# Configurable Limits Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote five (or seven if you also want the medium-priority items) hardcoded limits to public configuration options so consumers can override defaults without forking. Today's defaults stay as defaults — this is purely additive surface.

**Architecture:** Five independent tasks, each one new property + DI plumbing + tests. Pattern A (RabbitMqOptions → ClientSettings → consumer-side TryGetValue, used for the two header knobs) mirrors the existing `PrefetchCount`/`MessageSize`/`RetryCount` flow. Pattern B (BusConfiguration → DI-injected → class reads property, used for the three core knobs) mirrors the `DisposeTimeout`/`MaxRoutingSlipHops` flow. Both patterns are already established in the codebase; this plan adds five more knobs on the same rails.

**Tech Stack:** Same as the pre-release-fixes plan. .NET 8 / .NET 10 multi-target, xUnit, Moq. Build per project with `dotnet build src/<Project>/<Project>.csproj -m:1`; test with `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "..."`. Per the user's auto-memory, agents (subagents) must run all `dotnet` commands; do not run them from the main session.

**Docs update is in scope.** Each task's commit MUST include any relevant doc edits in `website/src/content/docs/` and (where relevant) `README.md`. The docs site is Astro Starlight (`.mdx` files). The two recurring docs targets are:

- **`website/src/content/docs/learn/operations/configuration.mdx`** — user-facing tour of the five configure delegates. Tasks 1-2 extend the "Typed RabbitMQ options overload" section; Tasks 3-5 extend the "Bus runtime behaviour" code sample and bullet list.
- **`website/src/content/docs/reference/configuration/itransportconfiguration.mdx`** (Tasks 1-2) and **`website/src/content/docs/reference/bus/ibusconfiguration.mdx`** (Tasks 3-5) — API-reference style. New properties get a row / bullet matching the existing style.
- **`website/src/content/docs/learn/messaging-patterns/streaming.mdx`** (Tasks 4-5 only) — mention the now-tunable stream caps where the doc discusses limits.

The `README.md` doesn't currently enumerate individual options; leave it untouched unless a task adds a new top-level concept the README's "What it does" pitch needs to mention. None of these five tasks rise to that bar.

Read each target file before editing to match its existing tone, code-fence style, and cross-reference conventions. Do NOT introduce new doc sections; extend existing ones.

---

## What this plan covers

| # | Constant | Default | New option | Pattern |
|---|---|---|---|---|
| 1 | [`DefaultMaxHeaderCount`](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L29) | 64 | `RabbitMqOptions.MaxHeaderCount` (int?) | A — ClientSettings |
| 2 | [`DefaultMaxHeaderValueBytes`](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L30) | 8192 | `RabbitMqOptions.MaxHeaderValueBytes` (int?) | A — ClientSettings |
| 3 | [`MaxInflightRequests`](src/ServiceConnect/Services/RequestReplyManager.cs#L19) | 10,000 | `IBusConfiguration.MaxInflightRequests` (int) | B — DI |
| 4 | [`MaxTotalStreamSize`](src/ServiceConnect/Services/MessageBusReadStream.cs#L15) | 100 MB | `IBusConfiguration.MaxStreamSizeBytes` (long) | B — DI |
| 5 | [`MaxActiveStreams`](src/ServiceConnect/Services/Processors/StreamProcessor.cs#L39) | 1000 | `IBusConfiguration.MaxActiveStreams` (int) | B — DI |

Optional Phase 2 (not detailed below — same patterns):
- Task 6 (optional): `StreamReassemblyTimeout` (5 min) + `StreamCleanupInterval` (1 min) → `IBusConfiguration`
- Task 7 (optional): `ConsumeContextPool.MaxPoolSize` (512) → `IBusConfiguration.ConsumeContextPoolMaxSize`

---

## File Structure

| File | Touched by task |
|---|---|
| `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs` | 1, 2 (properties + `Validate()` clauses) |
| `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQSettingKeys.cs` | 1, 2 (new constants) |
| `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQExtensions.cs` | 1, 2 (`ApplyToClientSettings`) |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` | 1, 2 (read from `ClientSettings`, pass to `RabbitMqHeaderValidator`) |
| `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqOptionsValidateTests.cs` | 1, 2 (extend with new validation cases) |
| `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostHeaderLimitsTests.cs` | 1, 2 (new — wiring test) |
| `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs` | 3, 4, 5 (new properties) |
| `src/ServiceConnect/Configuration/BusConfiguration.cs` | 3, 4, 5 (backing fields + freeze-guarded setters + validation in `Validate*Bus*` or ctor) |
| `src/ServiceConnect/ServiceConnectBuilder.cs` | 3, 4, 5 (extend `ValidateBus` if defaults need range checks) |
| `src/ServiceConnect/Services/RequestReplyManager.cs` | 3 (inject `IBusConfiguration`, replace const with field) |
| `src/ServiceConnect/Services/MessageBusReadStream.cs` | 4 (ctor param for max size, replace const) |
| `src/ServiceConnect/Services/Processors/StreamProcessor.cs` | 4, 5 (inject `IBusConfiguration`, pass max size to `MessageBusReadStream`, read `MaxActiveStreams`) |
| `src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.cs` | 3 (only if DI factory needs an explicit factory for `RequestReplyManager`) |
| `src/ServiceConnect.UnitTests/Services/RequestReplyManagerMaxInflightConfigurableTests.cs` | 3 (new) |
| `src/ServiceConnect.UnitTests/Services/MessageBusReadStreamMaxSizeConfigurableTests.cs` | 4 (new) |
| `src/ServiceConnect.UnitTests/Processors/StreamProcessorMaxActiveStreamsConfigurableTests.cs` | 5 (new) |

---

## Pre-flight (every task)

1. `which dotnet` returns `/home/tim/.local/bin/dotnet`. If not, stop.
2. `git status` shows a clean tree (or only the files the current task touches).
3. Branch: `v7-clean-architecture` (or wherever you decide to land this).

---

## Task 1: `RabbitMqOptions.MaxHeaderCount`

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQSettingKeys.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQExtensions.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`
- Modify: `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqOptionsValidateTests.cs`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostHeaderLimitsTests.cs`

**Why:** A 64-header cap protects against memory exhaustion but rejects legitimate tracing-heavy messages. Make it a `RabbitMqOptions` knob with the existing default preserved.

- [ ] **Step 1: Read `RabbitMQSettingKeys.cs`** to find where the existing key constants live and the section to extend.

- [ ] **Step 2: Add the new setting-key constant**

Apply an Edit to `RabbitMQSettingKeys.cs` adding a new `public const string MaxHeaderCount = "RabbitMq.MaxHeaderCount";` constant. The exact `old_string` anchor depends on the file's current shape — read it first; pick the last existing constant (`NetworkRecoveryInterval` is the last in `RabbitMqOptions`, so its key is probably the last in `RabbitMQSettingKeys` too) and add after it. Use the same `RabbitMq.` namespace prefix all the other keys use.

- [ ] **Step 3: Add the `MaxHeaderCount` property to `RabbitMqOptions`**

Apply an Edit to `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs` adding the property after `NetworkRecoveryInterval`:

```csharp
/// <summary>
/// Maximum number of headers allowed on an inbound message. Defaults to 64. Inbound messages
/// with more headers are rejected (NACK'd to retry / dead-letter). Increase if your producers
/// legitimately stamp wider header sets (e.g. heavy distributed-tracing baggage); decrease
/// to tighten resource exhaustion defence on hostile inputs.
/// </summary>
public int? MaxHeaderCount { get; set; }
```

- [ ] **Step 4: Extend `RabbitMqOptions.Validate()`**

Apply an Edit to add a new validation clause inside `Validate()`:

```csharp
if (MaxHeaderCount is { } maxHeaderCount && maxHeaderCount < 1)
{
    errors.Add($"MaxHeaderCount must be positive (was {maxHeaderCount}).");
}
```

Insert this clause near the other integer-range checks for consistency.

- [ ] **Step 5: Wire `MaxHeaderCount` through `ApplyToClientSettings`**

Apply an Edit to `RabbitMQExtensions.cs` adding the new `SetClientSetting` line in the dictionary-write block (alongside the existing `RetryCount`, `MessageSize`, etc. lines):

```csharp
if (options.MaxHeaderCount.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.MaxHeaderCount, options.MaxHeaderCount.Value); }
```

- [ ] **Step 6: Read the setting in `RabbitMqConsumerHost` constructor**

Read `RabbitMqConsumerHost.cs:118-150` to confirm the current shape of `_maxInboundMessageSize` resolution and the `RabbitMqHeaderValidator` construction.

Apply an Edit to add a new field + read:

```csharp
// Existing:
//   private const int DefaultMaxHeaderCount = 64;
// Add a new resolved value alongside the existing _maxInboundMessageSize line.
```

Specifically:

1. Add a new private readonly field `private readonly int _maxHeaderCount;` next to `_maxInboundMessageSize`.
2. In the ctor body, add the resolution line (mirroring the `_maxInboundMessageSize` block):
   ```csharp
   _maxHeaderCount = settings.TryGetValue(RabbitMQSettingKeys.MaxHeaderCount, out var maxHeaderCountVal)
       ? Convert.ToInt32(maxHeaderCountVal, System.Globalization.CultureInfo.InvariantCulture)
       : DefaultMaxHeaderCount;
   ```
3. Update the `RabbitMqHeaderValidator` construction to pass `_maxHeaderCount` instead of `DefaultMaxHeaderCount`:
   ```csharp
   _validator = new RabbitMqHeaderValidator(
       retryHandler,
       _maxInboundMessageSize,
       _maxHeaderCount,      // was DefaultMaxHeaderCount
       DefaultMaxHeaderValueBytes,
       GetShutdownPublishToken,
       logger);
   ```

- [ ] **Step 7: Extend `RabbitMqOptionsValidateTests` with one new fact**

Apply an Edit adding a new `[Fact]`:

```csharp
[Fact]
public void Validate_NegativeMaxHeaderCount_ReturnsError()
{
    var options = new RabbitMqOptions { MaxHeaderCount = 0 };
    var errors = options.Validate();
    Assert.Contains(errors, e => e.Contains("MaxHeaderCount", System.StringComparison.Ordinal));
}
```

- [ ] **Step 8: Write a wiring test (new file)**

Create `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostHeaderLimitsTests.cs` with a single `[Fact]` that exercises the dictionary plumbing. Because the validator class is internal and the host constructs it, the test should: configure a `TransportConfiguration` with `RabbitMQSettingKeys.MaxHeaderCount = 200`, construct a `RabbitMqConsumerHost` through the same DI path the production wiring uses, and assert (via reflection on `_maxHeaderCount` if needed, or via observable rejection behaviour) that the configured value reaches the validator.

If reflection-based assertions feel fragile, prefer a behavioural test: pass a message with 100 headers; with the default 64-cap it would NACK; with the configured 200 it should be accepted. Look at existing `RabbitMqConsumerHostTests.cs` for the construction helper.

- [ ] **Step 9: Build the RabbitMQ project**

Delegate: `dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1`. Expect 0/0.

- [ ] **Step 10: Run all RabbitMQ tests + the new ones**

Delegate: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~RabbitMQ"`. Expect 0 failed.

- [ ] **Step 11: Commit**

```
feat(rabbitmq): make MaxHeaderCount configurable on RabbitMqOptions

The 64-header cap was hard-coded in the consumer host; tracing-heavy producers
would silently NACK on the default. Expose as RabbitMqOptions.MaxHeaderCount
(nullable int, defaults to the existing 64) and route through the ClientSettings
dictionary using the established RabbitMQSettingKeys pattern. Validates positive
at UseRabbitMQ setup; falls back to the default constant when unset.
```

---

## Task 2: `RabbitMqOptions.MaxHeaderValueBytes`

**Files:** same as Task 1 (RabbitMqOptions, RabbitMQSettingKeys, RabbitMQExtensions, RabbitMqConsumerHost, Validate tests, header-limits test).

**Why:** 8 KB per header limit may be too tight for large correlation payloads or too loose for hostile inputs — let operators tune.

Repeat the same shape as Task 1 with:
- Property: `public int? MaxHeaderValueBytes { get; set; }` (xmldoc explaining the per-header byte cap)
- Setting key: `public const string MaxHeaderValueBytes = "RabbitMq.MaxHeaderValueBytes";`
- `ApplyToClientSettings`: `if (options.MaxHeaderValueBytes.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.MaxHeaderValueBytes, options.MaxHeaderValueBytes.Value); }`
- Validation in `Validate()`: error if `< 1` (you may want a sane lower bound like 16 to prevent users setting "0" or "1"; pick a sensible floor and document it in the error message).
- Consumer-host read: add `_maxHeaderValueBytes` field, resolution block, pass to `RabbitMqHeaderValidator` ctor in place of `DefaultMaxHeaderValueBytes`.
- One new `Validate_…ReturnsError` fact in `RabbitMqOptionsValidateTests`.
- Either extend the same wiring test from Task 1 or add a second `[Fact]` covering the byte-value limit specifically.

- [ ] **Step 1-11: same shape as Task 1; substitute `MaxHeaderValueBytes` everywhere.**

- [ ] **Step 12: Commit**

```
feat(rabbitmq): make MaxHeaderValueBytes configurable on RabbitMqOptions

Per-header byte cap (default 8192) is now tunable for deployments with large
correlation/tracing values or stricter DoS hardening requirements. Mirrors
the MaxHeaderCount knob's wiring.
```

---

## Task 3: `IBusConfiguration.MaxInflightRequests`

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs`
- Modify: `src/ServiceConnect/Configuration/BusConfiguration.cs`
- Modify: `src/ServiceConnect/ServiceConnectBuilder.cs` (extend `ValidateBus`)
- Modify: `src/ServiceConnect/Services/RequestReplyManager.cs`
- Create: `src/ServiceConnect.UnitTests/Services/RequestReplyManagerMaxInflightConfigurableTests.cs`

**Why:** [RequestReplyManager.cs:13-19](src/ServiceConnect/Services/RequestReplyManager.cs#L13-L19) already has a comment saying *"Exposed as an internal const so a future BusConfiguration knob can override"*. The design anticipates this. High-concurrency request-fan scenarios (parallel saga calls, fan-out aggregations) can legitimately exceed 10,000 in-flight requests.

- [ ] **Step 1: Read `IBusConfiguration.cs` and `BusConfiguration.cs`**

Confirm the existing patterns for adding settable properties with `ThrowIfFrozen()` and the `<inheritdoc />` shape.

- [ ] **Step 2: Add property to `IBusConfiguration`**

Apply an Edit to add (after `AllowMissingProducer` from the pre-release-fixes branch):

```csharp
/// <summary>
/// Gets or sets the maximum number of in-flight request-reply exchanges before
/// <c>SendRequestAsync</c> / <c>SendRequestMultiAsync</c> throws
/// <see cref="InvalidOperationException"/> ("cap reached"). Defaults to 10,000.
/// Each in-flight request pins a Timer, CancellationTokenSource, and TaskCompletionSource;
/// the cap defends against unbounded memory growth from <see cref="Timeout.Infinite"/>
/// callers that never wake or hot loops of unawaited requests. Increase for genuine
/// high-concurrency request-fan workloads; decrease to harden against caller bugs.
/// </summary>
int MaxInflightRequests { get; set; }
```

- [ ] **Step 3: Add property to `BusConfiguration`**

Backing field (near other ints):
```csharp
private int _maxInflightRequests = 10_000;
```

Property:
```csharp
/// <inheritdoc />
public int MaxInflightRequests { get => _maxInflightRequests; set { ThrowIfFrozen(); _maxInflightRequests = value; } }
```

- [ ] **Step 4: Extend `ServiceConnectBuilder.ValidateBus`**

Read the existing validation pattern (look for `MaxRoutingSlipHops` validation as a sibling). Add a `MaxInflightRequests > 0` check; throw `InvalidOperationException` with a clear message if violated.

- [ ] **Step 5: Refactor `RequestReplyManager` to consume the option**

Apply an Edit:
- Change the primary constructor signature to add `IBusConfiguration busConfig`:
  ```csharp
  internal sealed class RequestReplyManager(IMessageSerializer serializer, ISendMessagePipeline sendPipeline, IBusConfiguration busConfig) : IRequestReplyManager, IReplyStatusRequestReplyManager, IAsyncDisposable
  ```
- Remove the `internal const int MaxInflightRequests = 10_000;` line and the associated comment.
- Add `private readonly int _maxInflightRequests = busConfig?.MaxInflightRequests ?? throw new ArgumentNullException(nameof(busConfig));` (with the same defensive null check pattern used by other private fields in the file).
- Replace both call sites (`if (_pendingRequests.Count >= MaxInflightRequests)` at lines 52 and 198) with the field: `_pendingRequests.Count >= _maxInflightRequests`.
- Update the exception messages that interpolate `MaxInflightRequests` to use the field.

- [ ] **Step 6: Update DI registration if needed**

Read `src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.cs` and search for where `IRequestReplyManager` is registered. If the registration uses a typed binding (`services.TryAddSingleton<IRequestReplyManager, RequestReplyManager>()`), MS.DI resolves `IBusConfiguration` automatically — no edit needed. If it uses a factory, update the factory to pass `IBusConfiguration`.

- [ ] **Step 7: Write the new test**

Create `src/ServiceConnect.UnitTests/Services/RequestReplyManagerMaxInflightConfigurableTests.cs` with two facts:

```csharp
[Fact]
public async Task SendRequestAsync_AtConfiguredCap_Throws()
{
    var config = new BusConfiguration { MaxInflightRequests = 2 };
    // Construct RequestReplyManager with the config; fill 2 slots; assert the 3rd throws
    // with a message that names "2" (the configured value, NOT the old 10_000 const).
}

[Fact]
public async Task SendRequestAsync_DefaultCap_StillTenThousand()
{
    var config = new BusConfiguration(); // default
    Assert.Equal(10_000, config.MaxInflightRequests);
}
```

(Adapt the SendRequestAsync setup to match the existing `RequestReplyManagerTests.cs` pattern — find a helper or repeat the minimal mock wiring.)

- [ ] **Step 8: Build core + interfaces**

`dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1 && dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1`. Expect 0/0.

- [ ] **Step 9: Run the RequestReplyManager test suite**

`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~RequestReplyManager"`. Expect all pass.

- [ ] **Step 10: Watch for downstream callers of the now-removed `internal const`**

Grep: `grep -rn "RequestReplyManager.MaxInflightRequests\|MaxInflightRequests" src/ --include='*.cs' | grep -v '/bin/\|/obj/'`. The const was internal so callers SHOULD all be inside the framework. If any test files referenced the const directly (because of `InternalsVisibleTo`), update them to construct a `BusConfiguration` with a known value and read from it.

- [ ] **Step 11: Commit**

```
feat(bus): make MaxInflightRequests configurable on IBusConfiguration

RequestReplyManager's hard 10,000 in-flight cap is now tunable via
BusConfiguration.MaxInflightRequests. The comment at the original const
site already anticipated this. Default unchanged; high-concurrency
request-fan deployments can raise it; tight-budget hosts can lower it.
```

---

## Task 4: `IBusConfiguration.MaxStreamSizeBytes`

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs`
- Modify: `src/ServiceConnect/Configuration/BusConfiguration.cs`
- Modify: `src/ServiceConnect/ServiceConnectBuilder.cs` (validation)
- Modify: `src/ServiceConnect/Services/MessageBusReadStream.cs`
- Modify: `src/ServiceConnect/Services/Processors/StreamProcessor.cs`
- Create: `src/ServiceConnect.UnitTests/Services/MessageBusReadStreamMaxSizeConfigurableTests.cs`

**Why:** Hard 100 MB cap on stream reassembly. Some deployments stream larger payloads (file uploads, ML model artifacts); other memory-constrained deployments want it tighter.

- [ ] **Step 1-3: Add property to `IBusConfiguration` and `BusConfiguration`**

Property name: `long MaxStreamSizeBytes { get; set; }`. Default: `100L * 1024 * 1024` (100 MB). xmldoc: explain DoS surface, when to raise/lower.

- [ ] **Step 4: Extend `ValidateBus`**

`MaxStreamSizeBytes > 0` (or a sensible floor like 64 KB so a 0-byte stream isn't possible).

- [ ] **Step 5: Add ctor parameter to `MessageBusReadStream`**

Current shape (per `MessageBusReadStream.cs:13`): `internal sealed class MessageBusReadStream(string sequenceId)`.

Change to: `internal sealed class MessageBusReadStream(string sequenceId, long maxTotalStreamSize)`. Remove the `private const long MaxTotalStreamSize = 100 * 1024 * 1024;` and use the constructor parameter as a `private readonly long _maxTotalStreamSize` field instead. Update the two reference sites (the throw at line 108 and the comparison at line 105).

The exception message at line 108 currently reads "Stream exceeds maximum size of {MaxTotalStreamSize / (1024 * 1024)} MB." — keep it but use the field.

- [ ] **Step 6: Pass the size from `StreamProcessor`**

`StreamProcessor` constructs `new MessageBusReadStream(sequenceId)` at line 153. Add a constructor parameter `IBusConfiguration busConfig` to `StreamProcessor` (it currently takes 6 services — adding one more is fine), store it as a private field, and pass `_busConfig.MaxStreamSizeBytes` to `MessageBusReadStream`'s constructor.

- [ ] **Step 7: Update DI registration if needed**

`StreamProcessor` is registered at `ServiceCollectionExtensions.cs:117` via `services.TryAddSingleton<StreamProcessor>();`. The new `IBusConfiguration` parameter resolves automatically. No edit needed unless the factory pattern is in use.

- [ ] **Step 8: Write the new test**

Create `src/ServiceConnect.UnitTests/Services/MessageBusReadStreamMaxSizeConfigurableTests.cs`:

```csharp
[Fact]
public async Task Write_ExceedsConfiguredCap_Throws()
{
    var stream = new MessageBusReadStream("seq-1", maxTotalStreamSize: 1024);
    // Write 1025 bytes via the existing Write API; assert InvalidOperationException
    // with message including "1 KB" or "1024".
}

[Fact]
public async Task Write_WithinConfiguredCap_Succeeds()
{
    var stream = new MessageBusReadStream("seq-1", maxTotalStreamSize: 1024);
    // Write 100 bytes; no exception.
}
```

Look at `MessageBusReadStreamTests.cs` for the existing Write helper pattern. Reproduce its style.

- [ ] **Step 9: Build core**

`dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1`. Expect 0/0.

- [ ] **Step 10: Run stream tests + watchpoint for existing tests**

`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~MessageBusReadStream|FullyQualifiedName~StreamProcessor"`. 

The existing `MessageBusReadStreamTests.cs` likely constructs `new MessageBusReadStream(sequenceId)` directly — those calls will now fail to compile. Update each call site to pass `maxTotalStreamSize: 100L * 1024 * 1024` (preserving prior behaviour), OR add a default-value parameter `long maxTotalStreamSize = 100L * 1024 * 1024` to the constructor so the existing tests don't need touching. **Recommended:** add the default — minimises diff churn.

- [ ] **Step 11: Commit**

```
feat(bus): make MaxStreamSizeBytes configurable on IBusConfiguration

Stream reassembly's 100 MB cap is now tunable. Default preserved; deployments
streaming larger artifacts (file uploads, ML models) can raise; memory-
constrained hosts can lower. StreamProcessor injects the value into each
MessageBusReadStream instance.
```

---

## Task 5: `IBusConfiguration.MaxActiveStreams`

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs`
- Modify: `src/ServiceConnect/Configuration/BusConfiguration.cs`
- Modify: `src/ServiceConnect/ServiceConnectBuilder.cs` (validation)
- Modify: `src/ServiceConnect/Services/Processors/StreamProcessor.cs` (read property)
- Create: `src/ServiceConnect.UnitTests/Processors/StreamProcessorMaxActiveStreamsConfigurableTests.cs`

**Why:** 1000 concurrent partial-stream slots may be a tight ceiling for high-concurrency file-transfer workloads.

- [ ] **Step 1-3: Add property to `IBusConfiguration` and `BusConfiguration`**

`int MaxActiveStreams { get; set; }`. Default: `1000`. xmldoc references the DoS-defence purpose and how exceeding the cap behaves (rejection + warning log per the existing code).

- [ ] **Step 4: Extend `ValidateBus`** — positive int.

- [ ] **Step 5: Refactor `StreamProcessor`**

`StreamProcessor` now takes `IBusConfiguration` (added in Task 4). Read `_busConfig.MaxActiveStreams` into a `private readonly int _maxActiveStreams` field in the constructor. Replace the const usage at the admission check (`if (newCount > MaxActiveStreams)` at line 146) with the field. Update the warning log at line 149 to reference the field.

The `MaxActiveStreams` const at line 39 can be removed (or kept as `DefaultMaxActiveStreams` for the BusConfiguration's default — pick one source of truth, prefer the BusConfiguration default).

- [ ] **Step 6: Write the new test**

Create `src/ServiceConnect.UnitTests/Processors/StreamProcessorMaxActiveStreamsConfigurableTests.cs`:

```csharp
[Fact]
public async Task ProcessAsync_AtConfiguredCap_Rejects()
{
    var config = new BusConfiguration { MaxActiveStreams = 2 };
    // Construct StreamProcessor with config; admit 2 partial streams; the 3rd is rejected.
}
```

Look at the existing `StreamProcessorTests.cs` or sibling tests for the admission-flow helper.

- [ ] **Step 7: Build core**

`dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1`. Expect 0/0.

- [ ] **Step 8: Run all StreamProcessor tests**

`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~StreamProcessor"`. Expect 0 failed.

- [ ] **Step 9: Commit**

```
feat(bus): make MaxActiveStreams configurable on IBusConfiguration

StreamProcessor's 1000-slot admission cap is now tunable. Default preserved;
high-concurrency file-transfer deployments can raise; tight-memory hosts
can lower.
```

---

## Final smoke (after all five tasks)

- [ ] **F1:** per-project build sweep (Interfaces, ServiceConnect, Client.RabbitMQ, Persistence.MongoDb, Persistence.InMemory, Telemetry, HealthChecks). All 0/0.
- [ ] **F2:** `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1`. All pass.
- [ ] **F3:** `git log --oneline -8` — confirm five new commits with `feat(...)` prefixes, each with a Claude co-author trailer.

---

## Optional Phase 2 (not in this plan body — only if you want them)

If you want the medium-priority knobs too, they follow the identical patterns above:

### Task 6: stream timing knobs

Add `IBusConfiguration.StreamReassemblyTimeout` (default `TimeSpan.FromMinutes(5)`) and `IBusConfiguration.StreamCleanupInterval` (default `TimeSpan.FromMinutes(1)`). `StreamProcessor` reads them and passes the cleanup interval to `_timeProvider.CreateTimer(...)` at line 64; passes the timeout to `EvictStaleStreams` at line 64's callback target. Same plan shape as Task 5.

### Task 7: ConsumeContextPool capacity

Add `IBusConfiguration.ConsumeContextPoolMaxSize` (default `512`). `ConsumeContextPool` currently has no constructor injection — change `internal sealed class ConsumeContextPool` to accept `IBusConfiguration` in a primary or explicit constructor, read the value, and use it at the existing const usage site (line 38). Same plan shape as Task 5.

Each is one task / one commit / ~5 files touched. Add them to the implementation queue if scope appetite allows.
