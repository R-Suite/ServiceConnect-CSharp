# Phase 4 — v8 Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a coherent v8 release of ServiceConnect that resolves all six remaining architecture-review findings — five additive/internal improvements plus one deliberate breaking change (uniform outgoing-filter-Stop behavior across publish/send/route paths).

**Architecture:** Eight task-commits within one logical PR. Tasks 2-7 each address one finding; Task 8 bumps the version + updates docs. The breaking change (filter-Stop uniform throw) lands LAST so the test suite stays consistent through the additive items first, and so reviewers can isolate the only breaking-change diff. End-of-phase gate (Task 9) runs the full verification matrix.

**Tech Stack:** C# 12/14 (multi-target net8.0/net10.0; tests net10.0), xUnit 2.9, Moq 4.20, `Microsoft.Extensions.TimeProvider.Testing`, `Microsoft.Extensions.Diagnostics.Testing`, `Microsoft.Extensions.Options`. No new package dependencies.

---

## Phase-wide rules (apply to EVERY task)

Same rules as Phase 1-3 — see [docs/reviews/2026-05-17-refactor-phasing.md](../../reviews/2026-05-17-refactor-phasing.md). Summary:

1. **Validate before implementing.** Step 1 of every task. If a finding is no longer real, append to `docs/reviews/2026-05-17-rejected-findings.md` and skip.
2. **`dotnet` only from implementer subagent**, never main session. **`-m:1`** at MSBuild level. **Per-csproj only**.
3. **Per-change `superpowers:requesting-code-review`** before commit.
4. **One task = one commit.** Scoped prefix, present-tense, Claude co-author trailer.
5. **No ticket / phase / "fixes Xxx" framing in source comments** (per project CLAUDE.md).
6. **Behaviour-preserving** for tasks 2-6 (internal/additive). **Behaviour-CHANGING** for task 7 (uniform throw on filter-Stop is deliberate breaking change for v8).
7. **Existing test suites are the regression net.** Tasks 2-6 should pass the full 1644 baseline. Task 7 will REQUIRE updating some tests that currently assert silent-stop behavior — the plan estimates 5-15 test updates.

---

## End-of-phase verification gate (Task 9)

- [ ] Final code review on `<phase-4-base>..HEAD`
- [ ] Full unit-test suite (-m:1)
- [ ] Full E2E suite (-m:1, Docker)
- [ ] Serialization-compat tests (-m:1)
- [ ] All example apps build (-m:1 each)
- [ ] Docs/README updated: version 7.0.0 → 8.0.0 in Directory.Build.props; new "v8 breaking changes" entry in `website/src/content/docs/releases.mdx`; README's "supported runtimes" / current version reference (if any) updated
- [ ] LOC sanity check on all changed files

---

## File structure

| File | Change | Responsibility |
|---|---|---|
| `src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs` | Modify | Replace `bool logAsMaxRetries` flag with `PublishErrorReason` enum. (Task 2) |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs` | Modify | Split `_lifecycleSemaphore` into `_startupSemaphore` + `_disposeSemaphore`. (Task 3) |
| `src/ServiceConnect/Services/IConsumeContextAccessor.cs` | Create | Interface contract (CurrentHeaders + Push). (Task 4) |
| `src/ServiceConnect/Services/IConsumeScopeAccessor.cs` | Create | Interface contract (Current + Push). (Task 4) |
| `src/ServiceConnect/Services/ConsumeContextAccessor.cs` | Modify | Implement `IConsumeContextAccessor`. (Task 4) |
| `src/ServiceConnect/Services/ConsumeScopeAccessor.cs` | Modify | Implement `IConsumeScopeAccessor`. (Task 4) |
| `src/ServiceConnect/Bus.cs` | Modify | Constructor takes interfaces, not concretes. (Task 4) |
| `src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.cs` | Modify | Bind interfaces to concrete classes as singletons. (Task 4) |
| `src/ServiceConnect.Interfaces/Bus/IProducer.cs` | Modify | Add `bool SupportsRoutingKey { get; } => false;` capability. (Task 5) |
| `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` | Modify | Override `SupportsRoutingKey => true`. (Task 5) |
| `src/ServiceConnect/Bus.cs` | Modify | Emit once-per-bus warning when routing key supplied to a non-supporting producer. (Task 5) |
| `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs` | Create | Typed POCO matching `RabbitMQSettingKeys`. (Task 6) |
| `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQExtensions.cs` | Modify | New `UseRabbitMq(Action<RabbitMqOptions>?)` overload. (Task 6) |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` + `Producer/ProducerConnection.cs` | Modify | Read typed options first; fall back to ClientSettings dict. (Task 6) |
| `src/ServiceConnect/Bus.cs` | Modify | Filter-Stop `return` → `throw OutgoingFiltersBlockedException` at 4 sites. (Task 7) |
| `src/ServiceConnect.Interfaces/Bus/IBus.cs` | Modify | xmldoc: drop "silent return" wording on publish/send/route; add `<exception cref>` to all 4. (Task 7) |
| `src/ServiceConnect.UnitTests/BusTests.cs` etc. | Modify | Update filter-Stop tests to assert `ThrowsAsync<OutgoingFiltersBlockedException>` (estimate 5-15 tests). (Task 7) |
| `src/Directory.Build.props` | Modify | `<Version>7.0.0</Version>` → `<Version>8.0.0</Version>`. (Task 8) |
| `website/src/content/docs/releases.mdx` | Modify | New v8 release entry describing the breaking change + new features. (Task 8) |
| `README.md` | Modify (if applicable) | Update any version-specific references. (Task 8) |

---

## Task 1: Validate scope of all 6 findings

**Investigation only — no code change, no commit.**

- [ ] **Step 1: Filter Stop call sites (Task 7 preview)**

```bash
cd /home/tim/source/ServiceConnect-CSharp
grep -n "return new ConsumeEventResult\|prep.Stopped\|FilterAction.Stop" src/ServiceConnect/Bus.cs
grep -rn "OutgoingFiltersBlockedException" src/ --include='*.cs' | head -20
```

Expected: in `Bus.cs`, four `if (prep.Stopped) { return; }` blocks inside `PublishAsync`, `SendAsync`, `SendToManyAsync`, `RouteAsync`. After Phase 2's extraction these now read approximately:
```csharp
var prep = await PrepareOutboundAsync(message, options?.Headers, cancellationToken).ConfigureAwait(false);
if (prep.Stopped) { return; }
```

Confirm 4 occurrences and report their line numbers.

- [ ] **Step 2: IProducer current shape (Task 5 preview)**

```bash
grep -n "SupportsRoutingKey" src/ServiceConnect.Interfaces/Bus/IProducer.cs src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs
```

Expected: zero matches. If matches found, the capability flag already exists.

- [ ] **Step 3: RabbitMqOptions current shape (Task 6 preview)**

```bash
ls src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs 2>/dev/null
grep -rn "ClientSettings\[" src/ServiceConnect.Client.RabbitMQ --include='*.cs' | wc -l
```

Expected: file does not exist; ~10-20 `ClientSettings[...]` read sites in the codebase that the typed options will eventually displace.

- [ ] **Step 4: Consumer lifecycle semaphore (Task 3 preview)**

```bash
grep -cn "_lifecycleSemaphore" src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs
```

Expected: 5-10 occurrences (declaration + Wait/Release pairs in StartConsumingAsync + DisposeAsync).

- [ ] **Step 5: Accessor types' current visibility (Task 4 preview)**

```bash
grep -n "^internal sealed class ConsumeContextAccessor\|^internal sealed class ConsumeScopeAccessor" src/ServiceConnect/Services/
ls src/ServiceConnect/Services/IConsumeContextAccessor.cs src/ServiceConnect/Services/IConsumeScopeAccessor.cs 2>/dev/null
```

Expected: classes are `internal sealed`; new interface files do not exist.

- [ ] **Step 6: MessageRetryHandler bool flag (Task 2 preview)**

```bash
grep -cn "logAsMaxRetries" src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs
```

Expected: ~6 occurrences (3 caller sites + parameter + 2 if-branches).

- [ ] **Step 7: Record baseline LOC for all changed files**

```bash
wc -l src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs \
       src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs \
       src/ServiceConnect/Services/ConsumeContextAccessor.cs \
       src/ServiceConnect/Services/ConsumeScopeAccessor.cs \
       src/ServiceConnect/Bus.cs \
       src/ServiceConnect.Interfaces/Bus/IProducer.cs \
       src/ServiceConnect.Interfaces/Bus/IBus.cs \
       src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs
```

Note the values — they become the end-of-phase LOC sanity check baseline.

- [ ] **Step 8: Report**

Report Status: DONE with all six findings' confirmed shapes. No commit.

---

## Task 2: `MessageRetryHandler` — replace `bool logAsMaxRetries` with `PublishErrorReason` enum

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs`

**Why this task is first:** smallest internal change, builds momentum, no public API.

- [ ] **Step 1: Validate**

Re-read `src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs:60-180`. Confirm:
- `HandleFailureAsync` line 65 calls `PublishErrorAsync(..., logAsMaxRetries: false, ...)` for malformed RetryCount header.
- `HandleFailureAsync` line 99 calls `PublishErrorAsync(..., logAsMaxRetries: true, ...)` for max retries exceeded.
- `HandleTerminalFailureAsync` line 109 calls `PublishErrorAsync(..., logAsMaxRetries: false, ...)` for permanently-invalid payload.
- `PublishErrorAsync` declares `bool logAsMaxRetries` at line ~117 and branches at lines ~138 + ~159.

- [ ] **Step 2: Add the enum**

Inside `MessageRetryHandler.cs`, near the top of the class (above the field declarations or as a nested type — pick whichever matches the existing file's style; if the file has no nested types, declare at the top of the namespace), add:

```csharp
/// <summary>
/// Why the message is being published to the error exchange. Drives log-message wording so
/// operators can distinguish "retry budget exhausted" from "header corruption" from
/// "permanently invalid payload" at a glance.
/// </summary>
internal enum PublishErrorReason
{
    /// <summary>Retry counter reached the configured maximum; this is the normal final-attempt path.</summary>
    MaxRetriesExceeded,

    /// <summary>The inbound message's RetryCount header was negative, non-numeric, or above the configured cap. Route to error instead of looping.</summary>
    MalformedRetryCountHeader,

    /// <summary>Payload was rejected at deserialise time (JsonException-class). Retrying produces the same failure; route directly to error.</summary>
    PermanentlyInvalidPayload,
}
```

- [ ] **Step 3: Update `PublishErrorAsync` signature and body**

Replace the parameter `bool logAsMaxRetries` with `PublishErrorReason reason`. Replace the two `if (logAsMaxRetries)` branches with `switch (reason)` patterns:

```csharp
private async Task PublishErrorAsync(
    IChannel channel,
    BasicDeliverEventArgs args,
    Dictionary<string, object> headers,
    Exception? ex,
    PublishErrorReason reason,
    CancellationToken cancellationToken)
{
    if (ex != null)
    {
        HeaderHelpers.SetHeader(headers, HeaderKeys.Exception, JsonSerializer.Serialize(new
        {
            TimeStamp = _timeProvider.GetUtcNow().UtcDateTime,
            ExceptionType = ex.GetType().FullName,
            Message = HeaderHelpers.GetErrorMessage(ex)
        }));
    }

    if (_errorsDisabled)
    {
        // IQueueConfiguration.DisableErrors=true contract: "failed messages bypass the error
        // queue." Honour it here at the single PublishErrorAsync site so every reason path
        // (max retries, malformed header, permanently-invalid payload) skips the publish.
        // The caller acks the original delivery, the message is dropped, and operators see
        // the drop on the dedicated counter rather than the message landing in the error
        // queue they explicitly asked us not to use.
        LogDropDueToErrorsDisabled(reason, ex, args);
        ServiceConnectMeter.AddRetryDrop(new TagList
        {
            { "messaging.system", "rabbitmq" },
            { "messaging.destination.name", _consumerQueueName },
            { "error.type", "errors-disabled" },
        });
        return;
    }

    LogPublishToErrorExchange(reason, ex, args);

    var errorProps = BasicPropertiesCopier.CreateCopy(args.BasicProperties, HeaderHelpers.ToNullableHeaders(headers));
    await channel.BasicPublishAsync(_errorExchange, string.Empty, true, errorProps, args.Body, cancellationToken).ConfigureAwait(false);
}

private void LogDropDueToErrorsDisabled(PublishErrorReason reason, Exception? ex, BasicDeliverEventArgs args)
{
    var messageId = args.BasicProperties.MessageId;
    switch (reason)
    {
        case PublishErrorReason.MaxRetriesExceeded:
            _logger.LogWarning(ex,
                "Max retries exceeded for MessageId {MessageId}; dropping per DisableErrors=true (no error-queue publish).",
                messageId);
            break;
        case PublishErrorReason.MalformedRetryCountHeader:
            _logger.LogWarning(ex,
                "Malformed RetryCount header for MessageId {MessageId}; dropping per DisableErrors=true (no error-queue publish).",
                messageId);
            break;
        case PublishErrorReason.PermanentlyInvalidPayload:
            _logger.LogWarning(ex,
                "Rejecting permanently invalid inbound message with MessageId {MessageId}; dropping per DisableErrors=true (no error-queue publish).",
                messageId);
            break;
    }
}

private void LogPublishToErrorExchange(PublishErrorReason reason, Exception? ex, BasicDeliverEventArgs args)
{
    var messageId = args.BasicProperties.MessageId;
    switch (reason)
    {
        case PublishErrorReason.MaxRetriesExceeded:
            if (ex != null)
                _logger.LogError(ex, "Max retries exceeded for MessageId {MessageId}", messageId);
            else
                _logger.LogError("Max retries exceeded for MessageId {MessageId}", messageId);
            break;
        case PublishErrorReason.MalformedRetryCountHeader:
            _logger.LogError(ex,
                "Malformed RetryCount header for MessageId {MessageId}; routing to error exchange.",
                messageId);
            break;
        case PublishErrorReason.PermanentlyInvalidPayload:
            _logger.LogError(ex, "Rejecting permanently invalid inbound message with MessageId {MessageId}", messageId);
            break;
    }
}
```

- [ ] **Step 4: Update the three caller sites**

Replace `logAsMaxRetries: false` (malformed-header path at line 65) with `reason: PublishErrorReason.MalformedRetryCountHeader`.

Replace `logAsMaxRetries: true` (max-retries path at line 99) with `reason: PublishErrorReason.MaxRetriesExceeded`.

Replace `logAsMaxRetries: false` (terminal-failure path at line 109) with `reason: PublishErrorReason.PermanentlyInvalidPayload`.

- [ ] **Step 5: Build + run tests**

```bash
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~MessageRetryHandler -m:1
```

Expected: build clean. All MessageRetryHandler tests pass (the existing tests verify log-message contents; check that the new log strings match the test assertions — if not, update either the tests or the log strings, preserving operator-visible alert-rule strings where possible).

If a test fails because the log-message string changed, decide:
- If the test asserts the exact string, and the new string is materially different, update the test to match the new string (e.g. "Malformed RetryCount header for MessageId..." vs the old "Malformed or out-of-range RetryCount header...").
- If the test asserts a substring (Contains), the new strings should mostly still match because all 3 reasons contain `"MessageId"`.

- [ ] **Step 6: Per-change code review**

`superpowers:requesting-code-review`. Focus: does the enum-based dispatch preserve the original log-level / log-text / drop-metric semantics?

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs
git commit -m "$(cat <<'EOF'
refactor(retry): replace logAsMaxRetries bool with PublishErrorReason enum

The internal PublishErrorAsync helper used a bool flag to switch
between "max retries exceeded" log wording and "other reason" log
wording — but the call sites pass false for two materially different
reasons (malformed RetryCount header vs permanently invalid payload).
Replace with an enum that names all three reasons explicitly; each
reason gets its own log message wording. No external behaviour change;
metrics and routing decisions are unaffected.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `Consumer` — split `_lifecycleSemaphore` into startup + dispose semaphores

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs`

**Finding context:** Currently one `_lifecycleSemaphore` guards both startup (StartConsumingAsync, BeginConsumingAsync, ConsumeMessageTypeAsync) and dispose. If startup is wedged (e.g. on a topology-provision retry loop), DisposeAsync can't acquire and SIGTERM is blocked. The fix: separate semaphores so startup and dispose race instead of serialise.

- [ ] **Step 1: Validate**

Re-read `Consumer.cs:30-50` (field declarations) and find every `_lifecycleSemaphore` site:

```bash
grep -n "_lifecycleSemaphore" src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs
```

Note each Wait/Release pair and which method owns it. The plan estimates:
- Declaration ~line 35
- StartConsumingAsync Wait ~line 127 + Release ~line 259, 270
- DisposeAsync Wait ~line 348 + Release ~line 414

Confirm against current line numbers.

- [ ] **Step 2: Split the semaphore**

Replace the single field with two fields:

```csharp
// Startup paths (StartConsumingAsync, BeginConsumingAsync, ConsumeMessageTypeAsync) acquire
// this. DisposeAsync uses a SEPARATE semaphore so a wedged startup cannot block SIGTERM
// shutdown — they touch different state (startup builds the _hosts collection; dispose
// drains it; ConcurrentBag handles the concurrent producer/consumer pattern).
private readonly SemaphoreSlim _startupSemaphore = new(1, 1);

// DisposeAsync acquires this. Bounded by BusConfiguration.DisposeTimeout so a wedged
// dispose path (e.g. broker handshake stuck) eventually surfaces rather than blocking
// process exit.
private readonly SemaphoreSlim _disposeSemaphore = new(1, 1);
```

Delete the old `private readonly SemaphoreSlim _lifecycleSemaphore = new(1, 1);` line.

- [ ] **Step 3: Update WaitAsync / Release call sites**

For each existing `_lifecycleSemaphore.WaitAsync(...)` / `_lifecycleSemaphore.Release()` site, replace with whichever semaphore matches the method:

- Inside `StartConsumingAsync` (around lines 127, 259, 270): `_startupSemaphore.WaitAsync` / `_startupSemaphore.Release()`.
- Inside `BeginConsumingAsync` / `ConsumeMessageTypeAsync` (find via grep if they also acquire the semaphore): `_startupSemaphore`.
- Inside `DisposeAsync` (around lines 348, 414): `_disposeSemaphore.WaitAsync` / `_disposeSemaphore.Release()`.

If you find a site that's ambiguous (e.g. a method that's called from BOTH startup and dispose paths), report NEEDS_CONTEXT — the original `_lifecycleSemaphore` was used to serialise all six methods, so a split needs careful per-call attribution.

- [ ] **Step 4: Update the dispose comment**

Find the comment block above the `_disposeSemaphore.WaitAsync(...)` call in `DisposeAsync` (around the previous line 344). It currently says "Bounded by DisposeTimeout so a wedged start cannot hang container shutdown." Rewrite to:

```csharp
// Bounded by DisposeTimeout so a wedged dispose itself cannot hang container shutdown.
// The semaphore is dedicated to DisposeAsync; a still-running startup acquires its own
// _startupSemaphore and runs concurrently with dispose. This is intentional — the
// pre-split single-semaphore design had StartConsumingAsync blocking SIGTERM when a
// topology-provision retry loop held the lock.
```

- [ ] **Step 5: Build + tests**

```bash
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~Consumer -m:1
```

Expected: build clean. All Consumer tests pass.

If a test specifically tests the OLD behaviour (e.g. asserts that DisposeAsync blocks until StartConsumingAsync completes), it must be UPDATED to reflect the new intentional concurrency. Such tests are likely rare (the lifecycle semaphore was an internal concern).

- [ ] **Step 6: Per-change code review**

`superpowers:requesting-code-review`. Focus: verify there's no method that legitimately needed mutual exclusion across startup AND dispose; verify the `_disposeSemaphore` timeout still applies; verify dispose ordering inside `DisposeAsync` is unchanged.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs
git commit -m "$(cat <<'EOF'
refactor(consumer): split lifecycle semaphore into startup + dispose

Previously a single _lifecycleSemaphore guarded both startup paths
(StartConsumingAsync, BeginConsumingAsync, ConsumeMessageTypeAsync)
and DisposeAsync. A wedged startup — for example a topology-provision
retry loop holding the lock — blocked DisposeAsync indefinitely,
making SIGTERM unresponsive in container shutdown.

Split into _startupSemaphore and _disposeSemaphore so the two race
instead of serialise. They touch different state: startup builds the
_hosts ConcurrentBag, dispose drains it. The ConcurrentBag handles
the concurrent producer/consumer pattern correctly.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Introduce `IConsumeContextAccessor` and `IConsumeScopeAccessor` interfaces

**Files:**
- Create: `src/ServiceConnect/Services/IConsumeContextAccessor.cs`
- Create: `src/ServiceConnect/Services/IConsumeScopeAccessor.cs`
- Modify: `src/ServiceConnect/Services/ConsumeContextAccessor.cs` (implement interface)
- Modify: `src/ServiceConnect/Services/ConsumeScopeAccessor.cs` (implement interface)
- Modify: `src/ServiceConnect/Bus.cs` (constructor takes interfaces)
- Modify: `src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.cs` (bind interface → concrete)

**Finding context:** Bus.cs takes `ConsumeContextAccessor` and `ConsumeScopeAccessor` as concrete types in its constructor — a DIP violation. The classes are `internal sealed` so this isn't a public-API change; promoting behind interfaces enables future test substitution and stays consistent with the rest of Bus's abstraction discipline.

- [ ] **Step 1: Validate**

```bash
grep -n "ConsumeContextAccessor\|ConsumeScopeAccessor" src/ServiceConnect/Bus.cs src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.cs
```

Confirm Bus.cs constructor takes concrete types; ServiceCollectionExtensions registers concrete types as singletons.

- [ ] **Step 2: Create `IConsumeContextAccessor.cs`**

```csharp
namespace ServiceConnect.Services;

/// <summary>
/// AsyncLocal-backed accessor for the headers of the inbound message currently being dispatched.
/// Used by outbound paths (Bus.RouteAsync, middleware) to read the inbound hop counter and
/// other carried context. Implementations must be safe for concurrent reads across messages
/// (the AsyncLocal's per-flow value isolates them).
/// </summary>
internal interface IConsumeContextAccessor
{
    /// <summary>The current inbound headers, or <see langword="null"/> when no consume flow is active.</summary>
    IReadOnlyDictionary<string, object>? CurrentHeaders { get; }

    /// <summary>
    /// Pushes a headers view as the current flow's context. The returned <see cref="IDisposable"/>
    /// restores the previous value when disposed; supports nested pushes.
    /// </summary>
    IDisposable Push(IReadOnlyDictionary<string, object> headers);
}
```

- [ ] **Step 3: Create `IConsumeScopeAccessor.cs`**

```csharp
namespace ServiceConnect.Services;

/// <summary>
/// AsyncLocal-backed accessor for the current per-message DI scope's <see cref="IServiceProvider"/>.
/// Lets filters/middleware/processors resolve scoped services from the same scope the
/// dispatcher established for the inbound message.
/// </summary>
internal interface IConsumeScopeAccessor
{
    /// <summary>The current scope's <see cref="IServiceProvider"/>. Throws when no scope is pushed.</summary>
    IServiceProvider Current { get; }

    /// <summary>Pushes a scope; returned <see cref="IDisposable"/> restores the previous value.</summary>
    IDisposable Push(IServiceProvider serviceProvider);
}
```

- [ ] **Step 4: Implement on the concrete classes**

Edit `src/ServiceConnect/Services/ConsumeContextAccessor.cs`. Change the class declaration to implement the interface:

```csharp
internal sealed class ConsumeContextAccessor : IConsumeContextAccessor
{
    // ... existing body unchanged
}
```

Same for `ConsumeScopeAccessor.cs`:

```csharp
internal sealed class ConsumeScopeAccessor : IConsumeScopeAccessor
{
    // ... existing body unchanged
}
```

The existing public members on both classes already match the interface signatures, so no method-level changes are needed.

- [ ] **Step 5: Update `Bus.cs` constructor**

In `Bus.cs`, change the two field declarations:

```csharp
// Before:
private readonly ConsumeContextAccessor _consumeContextAccessor;
private readonly ConsumeScopeAccessor _scopeAccessor;

// After:
private readonly IConsumeContextAccessor _consumeContextAccessor;
private readonly IConsumeScopeAccessor _scopeAccessor;
```

Change the constructor parameter types correspondingly:

```csharp
// Before (the relevant excerpt of the constructor signature):
//   ConsumeScopeAccessor scopeAccessor,
//   ConsumeContextAccessor? consumeContextAccessor = null,
// After:
//   IConsumeScopeAccessor scopeAccessor,
//   IConsumeContextAccessor? consumeContextAccessor = null,
```

The optional-default fallback at line 85 (`_consumeContextAccessor = consumeContextAccessor ?? new ConsumeContextAccessor();`) still works because `new ConsumeContextAccessor()` is assignable to `IConsumeContextAccessor`.

- [ ] **Step 6: Update DI registration**

Find the singleton registrations in `src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.cs`. They currently look approximately like:

```csharp
services.AddSingleton<ConsumeContextAccessor>();
services.AddSingleton<ConsumeScopeAccessor>();
```

Rewrite to bind the interface to the concrete:

```csharp
services.AddSingleton<IConsumeContextAccessor, ConsumeContextAccessor>();
services.AddSingleton<IConsumeScopeAccessor, ConsumeScopeAccessor>();
```

If there are other call sites in `ServiceCollectionExtensions*` files that read these accessors via `GetService<ConsumeXxxAccessor>()`, update them to `GetService<IConsumeXxxAccessor>()`. The Bus's constructor will receive the registered concrete via the interface.

- [ ] **Step 7: Build + tests**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1
```

Expected: build clean, all 1644 tests pass. Watch for test setup code that constructs Bus directly with concrete `ConsumeXxxAccessor` instances — those still work because the concrete type implements the interface.

- [ ] **Step 8: Per-change code review**

`superpowers:requesting-code-review`. Focus: confirm the DI registration matches lifetime (singleton), confirm no concrete-type leak in the constructor signature, confirm tests that construct Bus directly still compile.

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect/Services/IConsumeContextAccessor.cs \
        src/ServiceConnect/Services/IConsumeScopeAccessor.cs \
        src/ServiceConnect/Services/ConsumeContextAccessor.cs \
        src/ServiceConnect/Services/ConsumeScopeAccessor.cs \
        src/ServiceConnect/Bus.cs \
        src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.cs
git commit -m "$(cat <<'EOF'
refactor(bus): depend on IConsumeContextAccessor / IConsumeScopeAccessor

Bus's constructor previously took ConsumeContextAccessor /
ConsumeScopeAccessor concrete types alongside its other dependencies.
Promote behind internal interfaces so Bus depends on contracts, not
implementations, and so future test substitution doesn't require
subclassing the concrete sealed types. No external behaviour change;
DI registration binds interface → concrete as singletons (the existing
lifetime).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: `IProducer.SupportsRoutingKey` capability flag

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Bus/IProducer.cs` (add property with default `=> false`)
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` (override `=> true`)
- Modify: `src/ServiceConnect/Bus.cs` (once-per-bus LogWarning at first PublishAsync call where `options.RoutingKey` is set AND producer doesn't support it)

**Finding context:** The two routing-key overloads on IProducer use default-interface-method shims that silently drop the routing key. A third-party producer that doesn't override gets the no-op fallback with no diagnostic. Add a capability flag so callers can detect (and Bus can warn on) the silent-drop scenario.

- [ ] **Step 1: Validate**

```bash
grep -n "SupportsRoutingKey" src/ -r --include='*.cs'
```

Expected: zero matches.

- [ ] **Step 2: Add `SupportsRoutingKey` to `IProducer.cs`**

Add a new property declaration near the other capability properties (`MaximumMessageSize`, `IsHealthy`, etc.) on `IProducer`:

```csharp
/// <summary>
/// Gets whether this producer honours the <c>routingKey</c> parameter on
/// <see cref="PublishAsync(Type, ReadOnlyMemory{byte}, string?, IReadOnlyDictionary{string, string}?, CancellationToken)"/>.
/// Returns <see langword="false"/> for the default-interface-method shim — third-party
/// producers that haven't overridden the routing-key overload silently drop the key on
/// the wire. First-party transports (RabbitMQ) override to <see langword="true"/>.
/// </summary>
/// <remarks>
/// Bus uses this capability flag to emit a once-per-bus LogWarning when a caller supplies
/// <c>PublishOptions.RoutingKey</c> to a producer that doesn't honour it — without the
/// warning the routing-key intent is silently dropped on the wire and topic-exchange
/// dispatch never matches.
/// </remarks>
bool SupportsRoutingKey => false;
```

- [ ] **Step 3: Override in RabbitMQ Producer**

In `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs`, add the override near the other property implementations:

```csharp
/// <inheritdoc />
public bool SupportsRoutingKey => true;
```

- [ ] **Step 4: Wire the once-per-bus warning in `Bus.cs`**

In `Bus.cs`, find `PublishAsync` (around line 123 after Phase 2's extraction; locate via grep). The current shape:

```csharp
public async Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : Message
{
    ThrowIfDisposed();
    ArgumentNullException.ThrowIfNull(message);
    cancellationToken.ThrowIfCancellationRequested();
    var prep = await PrepareOutboundAsync(message, options?.Headers, cancellationToken).ConfigureAwait(false);
    if (prep.Stopped) { return; }
    var messageBytes = prep.Bytes;
    var headers = prep.Headers;
    // ... RoutingKey handling, SendContext construction
}
```

Add a once-flag field near the other Bus fields:

```csharp
private int _routingKeyShimWarned;
```

Insert the warning check at the start of the RoutingKey handling block (around line ~148, just before `if (options?.RoutingKey is { } routingKey)`):

```csharp
if (options?.RoutingKey is { Length: > 0 } &&
    _producer is not null &&
    !_producer.SupportsRoutingKey &&
    Interlocked.Exchange(ref _routingKeyShimWarned, 1) == 0)
{
    _logger.LogWarning(
        "PublishOptions.RoutingKey was supplied but the registered IProducer ({ProducerType}) reports SupportsRoutingKey=false; the key is being dropped on the wire. Update the transport implementation or remove the RoutingKey from PublishOptions to silence this warning.",
        _producer.GetType().FullName);
}
```

The `Interlocked.Exchange` ensures the warning fires exactly once per bus instance (the lifetime of the routing-key-drop hazard for that producer).

- [ ] **Step 5: Build + tests**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Bus|FullyQualifiedName~Producer" -m:1
```

Expected: build clean, all Bus + Producer tests pass.

- [ ] **Step 6: Per-change code review**

`superpowers:requesting-code-review`. Focus: confirm default-interface-method default value (`=> false`) is correct; confirm the once-flag uses Interlocked correctly; confirm the warning fires under the right conditions (routing-key supplied + producer reports false).

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Interfaces/Bus/IProducer.cs \
        src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs \
        src/ServiceConnect/Bus.cs
git commit -m "$(cat <<'EOF'
feat(producer): expose SupportsRoutingKey capability and warn on shim drop

IProducer gets a new bool SupportsRoutingKey property with a default-
interface-method default of false. Third-party transports that pre-date
the routing-key overload now identify themselves as not honouring the
parameter — Bus uses this to emit a once-per-bus LogWarning when a
caller supplies PublishOptions.RoutingKey to a producer that drops it
on the wire. The first-party RabbitMQ producer overrides to true. No
breaking change for callers; callers of IProducer that don't override
the property get the same shim-drop behaviour as before, but with a
loud warning instead of silent loss.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Typed `RabbitMqOptions` with `IOptions<>` binding

**Files:**
- Create: `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQExtensions.cs` (new overload accepting `Action<RabbitMqOptions>?`)
- Modify: read sites in `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` + `Producer/ProducerConnection.cs` to prefer typed options, fall back to dictionary

**Finding context:** RabbitMQ settings flow through `ITransportConfiguration.ClientSettings` (a `Dictionary<string, object>`). Keys are stringly-typed constants in `RabbitMQSettingKeys`. Typos surface at runtime. Add a typed POCO `RabbitMqOptions` bound via `IOptions<>`; preserve `ClientSettings` as a back-compat shim for v8 (v9 could remove it).

- [ ] **Step 1: Validate + discover call sites**

```bash
grep -rn "ClientSettings\[" src/ServiceConnect.Client.RabbitMQ --include='*.cs'
grep -rn "RabbitMQSettingKeys\." src/ServiceConnect.Client.RabbitMQ --include='*.cs'
ls src/ServiceConnect.Client.RabbitMQ/Configuration/
```

Expected: ~10-20 read sites across `RabbitMqConsumerHost`, `ProducerConnection`, `ConnectionFactoryBuilder`. Catalogue them with line numbers — these are the migration surface.

- [ ] **Step 2: Define the POCO**

Create `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs`:

```csharp
namespace ServiceConnect.Client.RabbitMQ.Configuration;

/// <summary>
/// Strongly-typed RabbitMQ transport options. Bound via <c>IOptions{RabbitMqOptions}</c> when
/// the consumer uses <c>UseRabbitMq(opts =&gt; ...)</c>; otherwise read from the legacy
/// <c>ITransportConfiguration.ClientSettings</c> string-keyed dictionary as a back-compat shim.
/// Property names mirror <see cref="RabbitMQSettingKeys"/> 1:1.
/// </summary>
/// <remarks>
/// In v8 both surfaces ship. In a future v9 release the <see cref="RabbitMQSettingKeys"/>
/// dictionary path may be removed; callers should prefer the typed options form for new
/// deployments.
/// </remarks>
public sealed record class RabbitMqOptions
{
    /// <summary>RabbitMQ TCP port. Defaults to 5672 for plain AMQP, 5671 for AMQPS.</summary>
    public int? Port { get; init; }

    /// <summary>Whether declared queues should be durable. Default: true.</summary>
    public bool? Durable { get; init; }

    /// <summary>Whether declared queues should be exclusive. Default: false.</summary>
    public bool? Exclusive { get; init; }

    /// <summary>Whether declared queues should be auto-deleted. Default: false.</summary>
    public bool? AutoDelete { get; init; }

    /// <summary>Additional x-arguments for the primary queue declaration.</summary>
    public IDictionary<string, object?>? Arguments { get; init; }

    /// <summary>Additional x-arguments for retry-queue declarations.</summary>
    public IDictionary<string, object?>? RetryQueueArguments { get; init; }

    /// <summary>Additional x-arguments for utility queues (audit, error).</summary>
    public IDictionary<string, object?>? UtilityQueueArguments { get; init; }

    /// <summary>Requested consumer prefetch count.</summary>
    public ushort? PrefetchCount { get; init; }

    /// <summary>Whether prefetch configuration should be disabled (consumer-side).</summary>
    public bool? DisablePrefetch { get; init; }

    /// <summary>Maximum inbound message body size, in bytes.</summary>
    public long? MessageSize { get; init; }

    /// <summary>Whether publisher confirms are enabled for outbound publishes.</summary>
    public bool? PublisherAcknowledgements { get; init; }

    /// <summary>Publisher retry attempt count.</summary>
    public int? RetryCount { get; init; }

    /// <summary>Delay between publish retries, in seconds (not milliseconds).</summary>
    public ushort? RetrySeconds { get; init; }

    /// <summary>Whether AMQP heartbeats are enabled. Default: true. See RabbitMQSettingKeys for the rationale.</summary>
    public bool? HeartbeatEnabled { get; init; }

    /// <summary>Heartbeat interval in seconds.</summary>
    public ushort? HeartbeatTime { get; init; }

    /// <summary>Maximum time to wait for a broker ack under publisher confirms. Default: 30s.</summary>
    public TimeSpan? PublishTimeout { get; init; }

    /// <summary>Maximum outstanding publisher confirms before back-pressure. Default: 256.</summary>
    public int? MaxOutstandingPublishConfirms { get; init; }

    /// <summary>Interval between auto-recovery attempts after a connection drop.</summary>
    public TimeSpan? NetworkRecoveryInterval { get; init; }
}
```

Every property is nullable so that absence in the typed options means "fall back to the dictionary or the runtime default."

- [ ] **Step 3: Add the typed-options overload in `RabbitMQExtensions.cs`**

Read the existing `UseRabbitMq` extension method to see its current shape. Add a new overload that accepts `Action<RabbitMqOptions>?`. Suggested skeleton (adapt to match the existing chain):

```csharp
/// <summary>
/// Configures the bus to use RabbitMQ as its transport. Settings are supplied via the
/// strongly-typed <see cref="RabbitMqOptions"/> action. Settings not set on the options
/// fall through to <see cref="ITransportConfiguration.ClientSettings"/> values (if any),
/// then to runtime defaults.
/// </summary>
public static ITransportConfiguration UseRabbitMq(this ITransportConfiguration transport,
    string connectionString,
    Action<RabbitMqOptions>? configure)
{
    var options = new RabbitMqOptions();
    configure?.Invoke(options);

    // Existing UseRabbitMq logic (queue/connection wiring) stays as-is.
    transport.UseRabbitMq(connectionString);

    // Bind the typed options on the DI container so RabbitMqConsumerHost / ProducerConnection
    // can read them via IOptions<RabbitMqOptions>. Implementation detail: stash on the
    // transport configuration's existing services collection, or expose a registration hook
    // depending on the bus's wiring contract — match the existing pattern.
    // The IOptions binding is `services.Configure<RabbitMqOptions>(opts => /* copy from local */)`.

    return transport;
}
```

The actual wiring depends on how `ITransportConfiguration` exposes its DI hook. If `UseRabbitMq` doesn't have access to `IServiceCollection`, route the typed options through the transport configuration's own bag (e.g. `transport.SetTypedOptions(options)`). Read the existing implementation to pick the right hook.

- [ ] **Step 4: Update read sites to prefer typed options, fall back to dictionary**

For each cataloged read site from Step 1, wrap the `ClientSettings[key]` access in a helper that prefers typed options:

```csharp
// Sketch — adapt to the actual codebase shape
private ushort GetPrefetchCount() =>
    _typedOptions?.PrefetchCount
        ?? (_clientSettings.TryGetValue(RabbitMQSettingKeys.PrefetchCount, out var v) ? Convert.ToUInt16(v) : (ushort)50);
```

Or, if there are many sites, extract a single `RabbitMqOptionsReader` helper that takes `IOptions<RabbitMqOptions>?` + `ITransportConfiguration` and exposes typed accessors for each setting. This is the cleanest design but more lift.

If the read-site count is small (<10), inline the read-with-fallback at each site. If large (>10), extract the reader.

- [ ] **Step 5: Build + tests**

```bash
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~RabbitMq -m:1
```

Expected: build clean, all RabbitMq tests pass (existing dictionary-based tests still work because the fallback preserves their behaviour).

If a test fails because a setting is no longer being read from the dictionary, it likely means the fallback chain is wrong — the typed-options read should return null (not a default) when unset, so the dictionary lookup runs.

- [ ] **Step 6: Per-change code review**

`superpowers:requesting-code-review`. Focus: confirm the fallback chain (typed options → dictionary → runtime default) at each read site; confirm `RabbitMqOptions` is `public` (it's a v8-public-surface addition); confirm no `ClientSettings[]` access was inadvertently removed.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs \
        src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQExtensions.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs \
        src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs
git commit -m "$(cat <<'EOF'
feat(rabbitmq): typed RabbitMqOptions via IOptions, dictionary back-compat

Adds a strongly-typed RabbitMqOptions POCO with properties mirroring
RabbitMQSettingKeys 1:1. A new UseRabbitMq(connectionString, Action<...>)
overload binds the options via IOptions<>. RabbitMqConsumerHost and
ProducerConnection prefer the typed options when set; fall back to the
existing ITransportConfiguration.ClientSettings dictionary when not.
Both surfaces ship in v8 — the dictionary remains for back-compat. v9
could remove ClientSettings.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Filter-Stop uniform throw (BREAKING — v8)

**Files:**
- Modify: `src/ServiceConnect/Bus.cs` (4 sites: PublishAsync, SendAsync, SendToManyAsync, RouteAsync)
- Modify: `src/ServiceConnect.Interfaces/Bus/IBus.cs` (xmldoc on the 4 methods)
- Modify: `src/ServiceConnect.UnitTests/BusTests.cs` + siblings (estimate 5-15 tests)

**Finding context:** Currently `PublishAsync`/`SendAsync`/`SendToManyAsync`/`RouteAsync` silently `return;` when outgoing filters return `FilterAction.Stop`. Request methods throw `OutgoingFiltersBlockedException`. Asymmetric. **User-confirmed fix:** make publish/send/route methods throw uniformly. **Breaking change** — operators who relied on silent stop will now see an exception. v8.

- [ ] **Step 1: Validate**

```bash
grep -n "if (prep.Stopped)" src/ServiceConnect/Bus.cs
```

Expected: 4 matches across PublishAsync, SendAsync, SendToManyAsync, RouteAsync. Each `if (prep.Stopped) { return; }` becomes `if (prep.Stopped) { throw new OutgoingFiltersBlockedException(...); }`.

Also catalogue tests that currently assert silent-stop behaviour:

```bash
grep -rn "FilterAction.Stop\|Stopped.*true" src/ServiceConnect.UnitTests --include='*.cs' | grep -v "BusOutboundPreparation" | head -30
```

(Exclude `BusOutboundPreparationTests` since those test the helper's contract — `Stopped=true` flag — not the caller's behaviour, so they're unaffected by Task 7.)

Note the affected test count. Plan estimate: 5-15.

- [ ] **Step 2: Update `Bus.cs` publish/send/route methods**

Find each `if (prep.Stopped) { return; }` and replace. Suggested message wording per call site:

**PublishAsync:**
```csharp
if (prep.Stopped)
{
    throw new OutgoingFiltersBlockedException("Outgoing filters blocked the published message.");
}
```

**SendAsync:**
```csharp
if (prep.Stopped)
{
    throw new OutgoingFiltersBlockedException("Outgoing filters blocked the sent message.");
}
```

**SendToManyAsync:**
```csharp
if (prep.Stopped)
{
    throw new OutgoingFiltersBlockedException("Outgoing filters blocked the multi-endpoint send.");
}
```

**RouteAsync:**
```csharp
if (prep.Stopped)
{
    throw new OutgoingFiltersBlockedException("Outgoing filters blocked the routed message.");
}
```

The 4 distinct messages let operators grep logs for which call-type was blocked. The exception type is uniform.

- [ ] **Step 3: Update `IBus.cs` xmldoc**

Find the 4 xmldoc blocks on `IBus.cs` (lines ~35, 53, 76, 160 mention "FilterAction.Stop causes this method to..."). Update each:

- Remove the "...causes this method to return without dispatching" wording.
- Add `<exception cref="Exceptions.OutgoingFiltersBlockedException">An outgoing filter blocked the message.</exception>`.

Example for `PublishAsync` (adapt to actual current xmldoc):

```csharp
/// <summary>
/// Publishes a message to all interested subscribers, optionally with publish-time options.
/// </summary>
/// <typeparam name="T">The message type, deriving from <see cref="Message"/>.</typeparam>
/// <param name="message">The message instance to publish.</param>
/// <param name="options">Optional publish options (routing key, additional headers, etc.).</param>
/// <param name="cancellationToken">Cancels the publish operation.</param>
/// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
/// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
/// <exception cref="Exceptions.OutgoingFiltersBlockedException">An outgoing filter returning <see cref="Pipelines.FilterAction.Stop"/> blocked the publish.</exception>
public Task PublishAsync<T>(T message, Options.PublishOptions? options = null, CancellationToken cancellationToken = default)
    where T : Message;
```

Same shape for the other 3 methods.

- [ ] **Step 4: Update affected tests**

For each test cataloged in Step 1 that currently asserts silent-stop behavior on publish/send/route paths, update from:

```csharp
[Fact]
public async Task PublishAsync_FilterStops_SilentlyReturns()
{
    // ... arrange filter returning Stop
    await bus.PublishAsync(message);
    // assert no exception thrown, producer not called
}
```

to:

```csharp
[Fact]
public async Task PublishAsync_FilterStops_ThrowsOutgoingFiltersBlocked()
{
    // ... arrange filter returning Stop
    var ex = await Assert.ThrowsAsync<OutgoingFiltersBlockedException>(() => bus.PublishAsync(message));
    Assert.Contains("publish", ex.Message, StringComparison.OrdinalIgnoreCase);
}
```

The test name should reflect the new behaviour; the assertion should verify the exception type and that the message identifies the call-type ("publish" / "send" / etc).

- [ ] **Step 5: Build + tests**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1
```

Expected: build clean. All 1644 tests pass after the 5-15 updates.

If a test STILL asserts silent-return after the update, it was missed in Step 4 — find it and update.

If a test passes unexpectedly without an update, it was probably asserting a different aspect of the flow (e.g. that producer was not called) — that assertion still works because the exception aborts before producer dispatch.

- [ ] **Step 6: Per-change code review**

`superpowers:requesting-code-review`. Focus: confirm all 4 Bus methods uniformly throw; confirm xmldoc updates match the new contract; confirm test updates assert exception type AND message content (so the call-type-distinct messages don't drift later).

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect/Bus.cs \
        src/ServiceConnect.Interfaces/Bus/IBus.cs \
        src/ServiceConnect.UnitTests/BusTests.cs \
        # ... + any sibling test files touched in Step 4
git commit -m "$(cat <<'EOF'
feat!(bus): throw OutgoingFiltersBlockedException uniformly across all dispatch paths

BREAKING CHANGE for v8. Previously PublishAsync, SendAsync,
SendToManyAsync, and RouteAsync silently returned when an outgoing
filter returned FilterAction.Stop, while the request methods
(SendRequestAsync, SendRequestMultiAsync, PublishRequestAsync) threw
OutgoingFiltersBlockedException. The asymmetry meant callers of the
fire-and-forget methods had no way to detect a blocked publish.

All four fire-and-forget methods now throw OutgoingFiltersBlockedException
with a call-type-specific message ("published" / "sent" / "multi-endpoint
send" / "routed") so operators can attribute blocks to the originating
API. The xmldoc declares the exception on every dispatch method
uniformly.

Migration: callers that intentionally swallowed silent-stop must
either remove the FilterAction.Stop-returning filter or wrap the
call in a try/catch for OutgoingFiltersBlockedException.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Version bump + docs

**Files:**
- Modify: `src/Directory.Build.props`
- Modify: `website/src/content/docs/releases.mdx`
- Modify: `README.md` (if version-specific references exist)

- [ ] **Step 1: Validate**

```bash
grep -n "<Version>" src/Directory.Build.props
grep -rn "v7\.\|7\.0\.0" website/src/content/docs/releases.mdx README.md | head -10
```

Expected: `<Version>7.0.0</Version>` in Directory.Build.props. README may have version-specific text; releases.mdx documents prior releases.

- [ ] **Step 2: Bump version**

In `src/Directory.Build.props`:

```xml
<!-- Before -->
<Version>7.0.0</Version>
<!-- After -->
<Version>8.0.0</Version>
```

- [ ] **Step 3: Add v8 changelog entry to `releases.mdx`**

Read the existing structure of `website/src/content/docs/releases.mdx` (it has entries for prior releases). Add a new entry at the top documenting v8. Suggested structure:

```mdx
## v8.0.0 — 2026-05-18

### Breaking changes

- **`IBus.PublishAsync` / `SendAsync` / `SendToManyAsync` / `RouteAsync` now throw `OutgoingFiltersBlockedException` when an outgoing filter returns `FilterAction.Stop`.** Previously these methods silently returned. The new behavior matches the request methods (`SendRequestAsync`, `SendRequestMultiAsync`, `PublishRequestAsync`) which already throw. Each method's exception message includes its call-type ("published" / "sent" / "multi-endpoint send" / "routed") so operators can attribute blocks. Callers that intentionally swallowed silent-stop must wrap dispatch calls in a try/catch for `OutgoingFiltersBlockedException`.

### New features

- **`IProducer.SupportsRoutingKey` capability flag.** Producers report whether they honour the `routingKey` overload of `PublishAsync`; Bus emits a once-per-bus `LogWarning` when a caller supplies `PublishOptions.RoutingKey` to a producer that drops it on the wire. The first-party RabbitMQ producer reports `true`; third-party transports that haven't overridden the routing-key overload get `false` (the default).
- **Typed `RabbitMqOptions` configuration.** A new `UseRabbitMq(connectionString, Action<RabbitMqOptions>?)` overload binds RabbitMQ settings via `IOptions<>`. Properties on `RabbitMqOptions` mirror `RabbitMQSettingKeys` 1:1. The existing `ITransportConfiguration.ClientSettings` dictionary remains as a back-compat shim — settings unset on the typed options fall through to the dictionary, then to runtime defaults. v9 may remove the dictionary path.

### Internal improvements

- **`MessageRetryHandler` uses an explicit `PublishErrorReason` enum** instead of a bool flag to distinguish max-retries-exceeded from malformed-header-rejection from permanently-invalid-payload routing. Log messages are now reason-specific.
- **Consumer lifecycle and dispose semaphores are independent.** A wedged `StartConsumingAsync` no longer blocks `DisposeAsync` — they race instead of serialise.
- **Bus depends on `IConsumeContextAccessor` / `IConsumeScopeAccessor` interfaces** instead of the concrete accessor types. No external behaviour change; future test substitution no longer requires subclassing.
```

- [ ] **Step 4: Update README if applicable**

```bash
grep -n "7\.0\.0\|v7" README.md
```

If matches are found, update version-specific text where appropriate. Most README content is version-neutral.

- [ ] **Step 5: Build the docs site (sanity check)**

If `website/` has a build command (likely `npm run build` or similar — check `website/package.json`):

```bash
ls website/package.json && cat website/package.json | grep -A 3 "\"scripts\""
```

If a build script exists and dependencies are installed, run it to ensure the new `releases.mdx` entry renders. Skip if not feasible in the current environment.

- [ ] **Step 6: Per-change code review**

`superpowers:requesting-code-review` on Task 8's diff. Focus: version bump applied; release notes accurately describe the v8 changes; migration guidance clear.

- [ ] **Step 7: Commit**

```bash
git add src/Directory.Build.props \
        website/src/content/docs/releases.mdx \
        README.md
git commit -m "$(cat <<'EOF'
chore(release): bump to v8.0.0 + document breaking changes

Version 7.0.0 → 8.0.0. Adds a v8.0.0 entry to the release notes
covering the breaking change (uniform OutgoingFiltersBlockedException
on filter-Stop), the two new public-API features (IProducer.SupportsRoutingKey
capability flag, typed RabbitMqOptions via IOptions<>), and the three
internal improvements (PublishErrorReason enum, split lifecycle
semaphores, accessor interfaces).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: End-of-phase verification gate

Same shape as Phase 1, 2, 3a, 3b, 3c end-of-phase gates. Each step is a delegated subagent.

- [ ] **Step 1: Final branch-wide code review** on `<phase-4-base>..HEAD` (where `<phase-4-base>` is the commit immediately before Task 1's run — Phase 3b's final commit `4078339d`).

- [ ] **Step 2: Full unit-test suite** — `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1`. Phase 3b baseline 1644; Phase 4 may have a slightly different count after Task 7 renames some tests (the renamed tests are still the same tests, just with different names). Expected: ≥1644 pass, 0 fail (treating the two known timing flakes as documented).

- [ ] **Step 3: Full E2E suite** — `dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`. Expected: 136/136.

- [ ] **Step 4: Serialization-compat tests** — Expected: 48/48.

- [ ] **Step 5: All example apps build** — Expected: 53/53 clean.

- [ ] **Step 6: Documentation site + README** — verify the new v8 entry in `releases.mdx` is well-formed; verify README updates (if any) are accurate.

- [ ] **Step 7: LOC sanity check** — compare current LOC of changed files to Task 1's baseline. Expected: small net increase (the typed options + accessor interfaces + capability flag add lines; the dead-code removals don't apply to Phase 4).

- [ ] **Step 8: Close Phase 4** — mark complete in TodoWrite. Branch is now at v8.0.0; ready for release if desired.

---

## Self-Review Checklist

1. **Spec coverage:**
   - User decision (filter-Stop throw uniformly): ✔ Task 7.
   - User decision (ONE coherent v8 release): ✔ One plan file, 9 task-commits.
   - All 6 findings from phasing doc: ✔ Tasks 2-7.
   - Version bump + docs: ✔ Task 8.
   - Verification gate: ✔ Task 9.

2. **Placeholder scan:** Every step has real code or a real command, with the exception of Task 6 Step 4 (which says "adapt to the actual codebase shape" for the read-site migration). This is justified because the read-site count and pattern are unknown until Task 6 Step 1's discovery; the implementer adapts based on what they find. Task 3 Step 3 similarly delegates to grep-discovery; same reasoning.

3. **Type consistency:**
   - `PublishErrorReason` enum (Task 2) used consistently.
   - `IConsumeContextAccessor` / `IConsumeScopeAccessor` (Task 4) named consistently.
   - `RabbitMqOptions` (Task 6) properties mirror `RabbitMQSettingKeys` 1:1.
   - `OutgoingFiltersBlockedException` (Task 7) referenced from the existing `Interfaces.Exceptions` namespace.

4. **Test patterns:** Existing test infrastructure (xUnit + Moq + FakeTimeProvider + FakeLogger) is the regression net for tasks 2-6. Task 7 updates 5-15 existing tests (it's the only behaviour-changing task; no new test infrastructure needed).

5. **Dotnet delegation:** Every `dotnet` step is delegated to the implementer subagent. Main session never invokes dotnet.

6. **Commit hygiene:** Each task = one commit. Scoped prefix, present-tense, Claude co-author trailer. Task 7's commit uses `feat!(...)` prefix to mark the breaking change (Conventional Commits semantic).

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-05-18-phase-4-v8-release.md`. Two execution options:

**1. Subagent-Driven (recommended)** — fresh implementer subagent per task + spec-compliance review + code-quality review per task. Same workflow that closed Phases 1, 2, 3a, 3b, 3c cleanly.

**2. Inline Execution** — `superpowers:executing-plans` with checkpoints.

Which approach?
