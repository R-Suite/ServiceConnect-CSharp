# Phase 3b — RabbitMqConsumerHost Channel-Lifecycle Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract channel lifecycle (the two `IChannel?` fields — `_model` and `_publishChannel` — plus their shutdown-event handlers `OnChannelShutdownAsync` / `OnPublishChannelShutdownAsync`) from the 964-line `RabbitMqConsumerHost` into a new internal collaborator `RabbitMqChannelHost`. The host class shrinks toward being a coordinator between admission, dispatch, retry, audit, broker-event-routing, and the channel host. Behaviour-preserving refactor.

**Architecture:** One new internal sealed class `RabbitMqChannelHost` in `src/ServiceConnect.Client.RabbitMQ/Consumer/`. The class owns the two channels and their shutdown event subscriptions. The consumer host accesses the channels via `internal` properties for the cases where it needs to call `BasicAck` / `BasicNack` / `BasicPublish` directly — this is the load-bearing simplification: we do NOT try to wrap every channel operation in a channel-host method (that would be a much larger refactor and lose useful direct-API surface for transport tests). The simpler win is "single owner for channel lifecycle and shutdown subscriptions"; the host code that consumes channels still does so directly via the property getters.

Note: this is a deliberate single-collaborator extraction. The broker-event-routing concerns (`OnConsumerShutdownAsync`, `OnConsumerUnregisteredAsync`, `OnConnectionShutdownAsync`, `OnConnectionBlockedAsync`, `OnConnectionUnblockedAsync`, `OnConsumerTagChangedAfterRecoveryAsync`) stay on `RabbitMqConsumerHost` for this phase. A future Phase 3b' could extract those into a `RabbitMqBrokerEventRouter` if the host class is still too large after Phase 3b.

**Tech Stack:** C# 12/14 (multi-target net8.0/net10.0; tests net10.0), xUnit 2.9, Moq 4.20. No new dependencies.

---

## Phase-wide rules (apply to EVERY task)

Same as Phase 1, 2, 3a, 3c. Summary:
1. **Validate before implementing.** Step 1 of every task.
2. **`dotnet` only from implementer subagent**, never main session. **`-m:1`** at MSBuild level. **Per-csproj only**.
3. **Per-change `superpowers:requesting-code-review`** before commit.
4. **One task = one commit.** Scoped prefix, present-tense, Claude co-author trailer.
5. **No ticket / phase / "fixes Xxx" framing in source comments**.
6. **Behaviour-preserving refactor.** Existing `RabbitMqConsumerHostTests.cs` (1709 lines, 70+ facts) is the regression net. The known flake `DisposeAsync_WhenAuditPublishStalls_CancelsPublishAtShutdownDeadline_AndLeavesMessageUnacked` (timing-sensitive under full-suite load) is documented from Phase 2's gate — if it fails in isolation, that's a real regression; if it only fails under load, it's the known issue.

---

## End-of-phase verification gate

- [ ] Final code review on the full Phase 3b diff
- [ ] Unit-test suite (`-m:1`) — full suite plus an isolated re-run of the known-flake test if it fails the full-suite run
- [ ] E2E suite (`-m:1`, Docker)
- [ ] Serialization-compat tests (`-m:1`)
- [ ] All example apps build (`-m:1` each)
- [ ] Docs/README current — Phase 3b is internal-only; expected NO UPDATE
- [ ] File-size sanity: `RabbitMqConsumerHost.cs` should drop ~80-120 LOC; new `RabbitMqChannelHost.cs` adds ~120-150 LOC. Combined total roughly flat.

---

## File structure

| File | Change | Responsibility |
|---|---|---|
| `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` | Modify | Coordinator. Holds reference to a `RabbitMqChannelHost` collaborator; accesses channels via channel-host properties. The consumer host still owns admission, dispatch, retry, audit, broker-event-routing for consumer/connection events. |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqChannelHost.cs` | Create | `internal sealed class RabbitMqChannelHost : IAsyncDisposable` — owns `_model` + `_publishChannel`, opens them, subscribes their shutdown events, exposes them as internal properties for direct-API consumption by the host, and disposes them in order. |

No public API change. No new test file required — the existing `RabbitMqConsumerHostTests` exercise the channel lifecycle end-to-end (consumer host construction → start → dispose). Behaviour-preservation is guarded by this suite.

---

## Task 1: Validate the scope

**No code changes — investigation only.**

- [ ] **Step 1: File-size and field inventory**

```bash
cd /home/tim/source/ServiceConnect-CSharp
wc -l src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
grep -n "private IChannel\?\|OnChannelShutdownAsync\|OnPublishChannelShutdownAsync\|_consumerCancelledByBroker" src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
```

Expected: file ~964 LOC. `_model` declared (around line 41) as `private IChannel? _model;`. `_publishChannel` declared (around line 45) as `private IChannel? _publishChannel;`. `OnChannelShutdownAsync` (around line 458) and `OnPublishChannelShutdownAsync` (around line 477) are both `private Task` methods on the host. `_consumerCancelledByBroker` is `private int` — the broker-cancelled flag.

If file size has drifted significantly (more than ~50 lines either direction) or the fields/methods are no longer at the expected approximate positions, STOP and report.

- [ ] **Step 2: Channel-access call-site inventory**

```bash
grep -n "_model\.\|_publishChannel\." src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs | wc -l
```

Note the count. This is the number of sites that will need to update from `_model.X` / `_publishChannel.X` to `_channelHost.Model.X` / `_channelHost.PublishChannel.X` (or equivalent). Expected: 20-40 sites. If significantly more, the extraction surface is larger than expected and the implementer should plan carefully.

- [ ] **Step 3: Confirm `RabbitMqChannelHost` doesn't already exist**

```bash
ls src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqChannelHost.cs 2>/dev/null
grep -rn "class RabbitMqChannelHost" src/ 2>/dev/null
```

Expected: both empty. If the class exists, STOP — someone has started the extraction.

- [ ] **Step 4: Known-flake test confirmation**

Run the known-flake test in isolation to establish baseline. If it fails in isolation here, the codebase has a real regression independent of Phase 3b and must be fixed first.

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName=ServiceConnect.UnitTests.RabbitMqConsumerHostTests.DisposeAsync_WhenAuditPublishStalls_CancelsPublishAtShutdownDeadline_AndLeavesMessageUnacked -m:1
```

Expected: PASS. If it fails, abort Phase 3b until the flake is fixed.

- [ ] **Step 5: Report**

Report Status: DONE with the actual file size, channel call-site count, and known-flake baseline. No commit.

---

## Task 2: Create `RabbitMqChannelHost` (without wiring)

**Files:**
- Create: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqChannelHost.cs`

**Finding context:** This task creates the new collaborator in isolation. No call sites change yet. The class compiles standalone but has zero consumers — Task 3 wires it.

**Why two tasks:** the create + wire path is large enough that splitting reduces the chance of "broken half-state" intermediate diffs. Task 2 lands as a clean dead-add; Task 3 lands as a clean migration.

- [ ] **Step 1: Validate**

Read the current shape of `_model` and `_publishChannel` initialization in `RabbitMqConsumerHost.cs`. They are populated inside `PrepareAsync` (around line 181) via `_connection.GetOrCreateChannelAsync(...)` or equivalent. Locate the exact initialization call:

```bash
grep -n "_model = \|_publishChannel = " src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
```

Note the initialization calls — they're the API surface the channel host needs to replicate.

Also locate the channel-shutdown subscription:

```bash
grep -n "ChannelShutdownAsync += \|ChannelShutdownAsync -= " src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
```

Each `+= OnChannelShutdownAsync` / `-= OnChannelShutdownAsync` pair (and same for the publish-channel variant) needs to migrate into the channel host's open/dispose methods.

- [ ] **Step 2: Author `RabbitMqChannelHost.cs`**

Create the file. Suggested skeleton (adapt to actual `IServiceConnectConnection` / `IChannel` shapes — read the existing code for the precise call signature):

```csharp
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ.Connection;

namespace ServiceConnect.Client.RabbitMQ.Consumer;

/// <summary>
/// Owns the consume channel (<c>Model</c>) and publish channel (<c>PublishChannel</c>) for a
/// single <c>RabbitMqConsumerHost</c>. Encapsulates channel acquisition, shutdown-event
/// subscription, and disposal ordering; the broker-cancellation flag flip lives here too
/// because it is driven by the channel-shutdown signals.
/// </summary>
/// <remarks>
/// Channel-shutdown events from the broker (queue deleted, policy expired, peer protocol
/// error) are NOT auto-recovered by RabbitMQ.Client v7. When a non-Application initiator
/// closes the channel, this class flips the cancellation flag so the consumer host's
/// IsCancelledByBroker accessor and BusConsumingHealthCheck report Unhealthy. Application-
/// initiated shutdown (host DisposeAsync / StopAsync) does NOT flip the flag.
///
/// The two channels are exposed as nullable properties; callers must null-check before
/// invoking channel operations (mirroring the host's existing pattern). Disposal is
/// idempotent; channels are closed in reverse-of-create order (publish channel first so
/// in-flight publishes drain before the consume channel goes away).
/// </remarks>
internal sealed class RabbitMqChannelHost : IAsyncDisposable
{
    private readonly IServiceConnectConnection _connection;
    private readonly ILogger _logger;
    private readonly string _queueName;
    private IChannel? _model;
    private IChannel? _publishChannel;
    private int _consumerCancelledByBroker;
    private int _disposed;

    internal RabbitMqChannelHost(IServiceConnectConnection connection, ILogger logger, string queueName)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _queueName = queueName ?? throw new ArgumentNullException(nameof(queueName));
    }

    /// <summary>The consume channel. Null until <see cref="OpenAsync"/> succeeds; null after disposal.</summary>
    internal IChannel? Model => _model;

    /// <summary>The publish channel. Null until <see cref="OpenAsync"/> succeeds; null after disposal.</summary>
    internal IChannel? PublishChannel => _publishChannel;

    /// <summary>
    /// True if a non-Application channel shutdown fired (broker tore down the channel, e.g.
    /// queue deleted, policy expired, peer protocol error). Latched until disposal.
    /// </summary>
    internal bool IsCancelledByBroker => Volatile.Read(ref _consumerCancelledByBroker) != 0;

    /// <summary>
    /// Opens the consume + publish channels and subscribes the shutdown event handlers.
    /// Idempotent on success — repeated calls return the already-open channels without
    /// re-opening.
    /// </summary>
    internal async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_model is not null && _publishChannel is not null)
        {
            return;
        }

        // TODO: implementer — replicate the exact channel-acquisition calls from
        // RabbitMqConsumerHost.PrepareAsync. The signature of GetOrCreateChannelAsync /
        // CreateChannelAsync varies by transport version; copy the host's existing pattern
        // verbatim so behaviour is preserved. Subscribe OnChannelShutdownAsync /
        // OnPublishChannelShutdownAsync after channel acquisition. Set both fields under
        // the same synchronization the host uses (likely none — channels are populated
        // exactly once in PrepareAsync; the host has no concurrent acquisition path).
    }

    private Task OnChannelShutdownAsync(object? sender, ShutdownEventArgs args)
    {
        if (args.Initiator != ShutdownInitiator.Application)
        {
            Interlocked.Exchange(ref _consumerCancelledByBroker, 1);
        }
        _logger.LogWarning(
            "AMQP channel shutdown for queue '{Queue}': {ReplyCode} {ReplyText} (initiator: {Initiator})",
            _queueName, args.ReplyCode, args.ReplyText, args.Initiator);
        return Task.CompletedTask;
    }

    private Task OnPublishChannelShutdownAsync(object? sender, ShutdownEventArgs args)
    {
        if (args.Initiator != ShutdownInitiator.Application)
        {
            Interlocked.Exchange(ref _consumerCancelledByBroker, 1);
        }
        _logger.LogWarning(
            "AMQP publish-channel shutdown for queue '{Queue}': {ReplyCode} {ReplyText} (initiator: {Initiator})",
            _queueName, args.ReplyCode, args.ReplyText, args.Initiator);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Unsubscribe shutdown handlers BEFORE close so a late shutdown signal doesn't fire
        // OnXxxShutdownAsync against a half-disposed host. Match the host's existing dispose
        // ordering for the channels themselves: publish channel first (so retry/audit
        // publishes that may still be in flight see a clean close), then the consume channel.
        if (_publishChannel is not null)
        {
            _publishChannel.ChannelShutdownAsync -= OnPublishChannelShutdownAsync;
            try { await _publishChannel.CloseAsync().ConfigureAwait(false); } catch { /* host's existing pattern */ }
            await _publishChannel.DisposeAsync().ConfigureAwait(false);
            _publishChannel = null;
        }

        if (_model is not null)
        {
            _model.ChannelShutdownAsync -= OnChannelShutdownAsync;
            try { await _model.CloseAsync().ConfigureAwait(false); } catch { /* host's existing pattern */ }
            await _model.DisposeAsync().ConfigureAwait(false);
            _model = null;
        }
    }
}
```

**Note the `// TODO: implementer` block in `OpenAsync`.** This is intentional — the actual channel-acquisition call signature depends on the existing `IServiceConnectConnection` shape, which the implementer should match exactly to preserve behaviour. The plan provides the skeleton; the implementer reads the existing `PrepareAsync` body and copies the call pattern verbatim. Do NOT leave the `TODO` in the final commit — replace it with the actual code before committing.

The `DisposeAsync` block's `try { CloseAsync } catch { }` pattern matches the host's existing convention (Channel.CloseAsync can throw if the channel is already closed or the connection is dead; the host swallows these). Mirror the host's exact swallow scope.

- [ ] **Step 3: Build the new file standalone**

```bash
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
```

Expected: 0 errors, 0 warnings. The new class has no consumers yet — it's a clean dead-add.

If the build fails, the most likely culprits are:
- `IChannel` API drift (the actual member names differ from the skeleton). Read the existing `RabbitMqConsumerHost.cs` and copy the literal API calls.
- Missing usings.

- [ ] **Step 4: Run the full RabbitMqConsumerHost* suite**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~RabbitMqConsumerHost -m:1
```

Expected: every pre-existing test passes (the dead-add doesn't touch any consumed code path). 70+ facts expected.

- [ ] **Step 5: Per-change code review**

`superpowers:requesting-code-review` on the new file in isolation. Focus: does the dispose ordering match the host's existing pattern? Are the channel-shutdown handlers byte-for-byte equivalent to the host's? Is the `_consumerCancelledByBroker` flag semantic preserved?

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqChannelHost.cs
git commit -m "$(cat <<'EOF'
refactor(rabbitmq): introduce RabbitMqChannelHost (dead add, no consumers yet)

Dead-add of the new internal sealed class that will own the consume +
publish channel lifecycle for RabbitMqConsumerHost. Channel-shutdown
event handlers and the broker-cancelled flag live here. Disposal
ordering (publish channel first, then consume channel) matches the
host's existing pattern. No call site uses this yet; Task 3 migrates
the host onto it. Behaviour-preserving prerequisite.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Migrate `RabbitMqConsumerHost` to use `RabbitMqChannelHost`

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`

**Finding context:** Replace the host's direct ownership of `_model` and `_publishChannel` with a `_channelHost` field of type `RabbitMqChannelHost`. The host accesses channels via `_channelHost.Model` and `_channelHost.PublishChannel`. The `OnChannelShutdownAsync` and `OnPublishChannelShutdownAsync` methods are removed from the host (they live on the channel host now). The `_consumerCancelledByBroker` flag also moves; host's `IsCancelledByBroker` reads `_channelHost.IsCancelledByBroker`.

This is the biggest task in Phase 3b. The migration affects 20-40 call sites. Land it as ONE commit so the diff is reviewable end-to-end and bisection lands on a coherent change.

- [ ] **Step 1: Validate**

Re-read the channel call sites from Task 1's grep. Spot-check 5 of them in context to confirm the pattern is uniform (each `_model.X` simply needs to become `_channelHost.Model.X`).

Also re-read `PrepareAsync` to confirm it's the sole channel-acquisition site. If channels are acquired elsewhere (e.g. lazy creation on first use), the migration needs to cover those sites too.

- [ ] **Step 2: Replace `_model` and `_publishChannel` fields with `_channelHost`**

In `RabbitMqConsumerHost.cs`:

Old:
```csharp
private IChannel? _model;
// ... and elsewhere ...
private IChannel? _publishChannel;
// ... and elsewhere ...
private int _consumerCancelledByBroker;
```

New (group them at the top):
```csharp
private readonly RabbitMqChannelHost _channelHost;
```

Remove the old `private IChannel? _model;` and `private IChannel? _publishChannel;` and `private int _consumerCancelledByBroker;` field declarations entirely.

- [ ] **Step 3: Construct `_channelHost` in the host constructor**

In the constructor (around line 106), after the existing field assignments, add:

```csharp
_channelHost = new RabbitMqChannelHost(_connection, _logger, _queueConfiguration.QueueName);
```

(Use whatever name the queue-name field/property is in this version of the host.)

- [ ] **Step 4: Move channel acquisition from `PrepareAsync` to `_channelHost.OpenAsync`**

In `PrepareAsync` (around line 181), replace the inline `_model = ...` / `_publishChannel = ...` channel-acquisition + subscribe pattern with a single call:

```csharp
await _channelHost.OpenAsync(cancellationToken).ConfigureAwait(false);
```

The exact code that was `_model = await _connection.GetOrCreateChannelAsync(...)` moves into `RabbitMqChannelHost.OpenAsync` (replacing the Task-2 TODO marker). Likewise for `_publishChannel`.

After the move, `PrepareAsync` no longer references `_model` or `_publishChannel` directly. The subscribe-shutdown-handler lines (`_model.ChannelShutdownAsync += OnChannelShutdownAsync;` etc.) are also gone from `PrepareAsync` — they live inside `OpenAsync` now.

- [ ] **Step 5: Update channel-access call sites throughout the host**

For each call to `_model.X` (where X is `BasicAckAsync`, `BasicNackAsync`, `BasicConsumeAsync`, `BasicCancelAsync`, `CloseAsync`, `IsOpen`, etc.), rewrite as `_channelHost.Model!.X` (note the `!` null-forgiving — `_channelHost.Model` is nullable but the host's existing call sites all assumed `_model` was non-null at the relevant point post-PrepareAsync; preserve that assumption with the null-forgiving operator at the same sites).

Alternative: where the host did a `if (_model != null)` null-check first, write `if (_channelHost.Model is { } model)` and use `model.X` inside the if. This is cleaner than `_channelHost.Model!.X` and matches the existing host's defensive style for these sites.

Same for `_publishChannel.X` → `_channelHost.PublishChannel!.X` (or pattern-matched local).

This is the bulk of the diff. Be methodical: use grep to find each remaining `_model.` and `_publishChannel.` reference and rewrite. After the rewrite, a final grep should return zero matches:

```bash
grep -n "_model\.\|_publishChannel\." src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
```

Expected: 0 matches.

- [ ] **Step 6: Remove `OnChannelShutdownAsync` and `OnPublishChannelShutdownAsync`**

Delete the two methods at lines 458-475 and 477-493 from `RabbitMqConsumerHost.cs`. They live on `RabbitMqChannelHost` now. (Note that the channel-host's `OpenAsync` does the `ChannelShutdownAsync +=` subscription, so the host no longer needs to do it in `PrepareAsync`.)

- [ ] **Step 7: Update `IsCancelledByBroker` accessor**

The host's `IsCancelledByBroker` (around line 311) currently reads `_consumerCancelledByBroker`. Rewrite as a forwarder:

```csharp
internal bool IsCancelledByBroker => _channelHost.IsCancelledByBroker;
```

The Volatile.Read pattern lives inside `RabbitMqChannelHost.IsCancelledByBroker` (Task 2's skeleton already includes it).

- [ ] **Step 8: Dispose `_channelHost` from the host's `DisposeAsync`**

In `RabbitMqConsumerHost.DisposeAsync`, where the host previously closed the channels inline, replace with a single line:

```csharp
await _channelHost.DisposeAsync().ConfigureAwait(false);
```

Place the line at the existing dispose-ordering position (after BasicCancel + drain, before the remaining cleanup). The exact insertion point: where the original code had `_publishChannel?.CloseAsync(...).ConfigureAwait(false)` and `_model?.CloseAsync(...).ConfigureAwait(false)` calls — replace BOTH with the single `_channelHost.DisposeAsync` call.

- [ ] **Step 9: Build**

```bash
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
```

Expected: 0 errors, 0 warnings. If errors fire, they will most likely be:
- A missed `_model.X` reference. Use grep to find.
- A test that accesses `_model` directly (search `src/ServiceConnect.UnitTests/` for `_model = ` or reflection-based access).
- A null-reference site where `_channelHost.Model` was used without `!` or pattern match.

- [ ] **Step 10: Run the full RabbitMqConsumerHost* test suite**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~RabbitMqConsumerHost -m:1
```

Expected: all 70+ pre-existing tests pass. The known-flake test (`DisposeAsync_WhenAuditPublishStalls_...`) may flake under load; if it fails, re-run in isolation to confirm it's the known issue.

- [ ] **Step 11: Per-change code review**

`superpowers:requesting-code-review`. Focus areas to highlight:
- Channel call-site count after migration: must be 0 references to `_model.` / `_publishChannel.` (the fields are gone).
- Dispose ordering: `_channelHost.DisposeAsync()` must fire at the same point in the host's DisposeAsync sequence where the inline channel closes used to be.
- `_consumerCancelledByBroker` flag: must read via `_channelHost.IsCancelledByBroker`. No new direct flag.
- The known-flake test result.

- [ ] **Step 12: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
git commit -m "$(cat <<'EOF'
refactor(rabbitmq): host delegates channel lifecycle to RabbitMqChannelHost

RabbitMqConsumerHost no longer owns the IChannel? _model / _publishChannel
fields nor the OnChannelShutdownAsync / OnPublishChannelShutdownAsync
event handlers nor the _consumerCancelledByBroker flag — all moved to
the channel-host collaborator added in the previous commit. The host
constructs a channel-host instance, calls OpenAsync from PrepareAsync,
accesses channels via the .Model / .PublishChannel property accessors
throughout, reads broker-cancellation via the channel-host's flag, and
disposes the channel-host in its own DisposeAsync. Behaviour preserved
exactly — guarded by the existing RabbitMqConsumerHostTests suite.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: End-of-phase verification gate

Same shape as Phase 1, 2, 3a, 3c end-of-phase gates.

- [ ] **Step 1: Final branch-wide code review** on the full Phase 3b diff.
- [ ] **Step 2: Full unit-test suite** — `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1`. Phase 2 baseline 1644; Phase 3b adds zero new tests. Expected: 1644 pass. If the known-flake test fails, re-run that single test in isolation (Phase 2 gate confirmed it passes 3/3 in isolation when not under full-suite scheduler pressure).
- [ ] **Step 3: Full end-to-end suite** — `dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`. Expected 136/136.
- [ ] **Step 4: Serialization-compat tests** — Expected 48/48.
- [ ] **Step 5: All example apps build** — Expected 53/53 clean.
- [ ] **Step 6: Documentation site + README** — Phase 3b is internal refactor; expected NO UPDATE NEEDED. Confirm `website/src/content/docs/` has no reference to `_model` / `_publishChannel` / `OnChannelShutdownAsync` as user-facing concepts.
- [ ] **Step 7: File-size sanity**:

```bash
wc -l src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs \
       src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqChannelHost.cs
```

Expected:
- `RabbitMqConsumerHost.cs`: ~840-880 LOC (was 964)
- `RabbitMqChannelHost.cs`: ~120-150 LOC
- Combined total: ~960-1030 (roughly flat — the channel-host's xmldoc and namespace boilerplate offset most of the savings).

The visible win is per-file cohesion, not total LOC.

- [ ] **Step 8: Close Phase 3b** — mark complete in TodoWrite. If the host file is still uncomfortably large (>900 LOC) after this phase, schedule Phase 3b' (broker-event-router extraction) as a follow-up — but only after the user reviews the post-3b state.

---

## Self-Review Checklist

1. **Spec coverage:**
   - Review's "extract RabbitMqChannelHost (channel lifecycle ownership)" → ✔ Tasks 2-3.
   - Review's "broker-event routing" extraction → **deferred to a potential Phase 3b'**. Explicit and intentional. The host class will still be large after Phase 3b but cleanly so — channel lifecycle is the highest-value cohesion-improvement target; the broker-event routing is small enough to leave for a later iteration if the file size still pains us.

2. **No public API change:**
   - `RabbitMqChannelHost` is `internal sealed`.
   - `RabbitMqConsumerHost`'s public surface unchanged.
   - `IsCancelledByBroker` is `internal` and the behaviour-equivalent post-refactor.

3. **No placeholders:** Every step has real code or a real command. The `// TODO: implementer` in Task 2 Step 2 is explicitly flagged as "replace before commit" and points to the existing host code as the reference — that's a deliberate placeholder for the implementer's read-and-copy step, NOT a "fill in later" placeholder for the controller.

4. **Test patterns:** Existing `RabbitMqConsumerHostTests.cs` (1709 lines, 70+ facts) is the regression net. Phase 3b adds no new tests because the refactor is behaviour-preserving and the existing tests exercise the full channel-lifecycle path end-to-end (PrepareAsync → BasicConsume → BasicAck → DisposeAsync).

5. **Dotnet delegation:** Every `dotnet` step runs in the implementer subagent.

6. **Commit hygiene:** Two commits total (Task 2: dead-add; Task 3: migration). Tasks 1 and 4 produce no commits.

7. **Risk mitigations:**
   - Splitting create + wire across two commits gives bisection a clean midpoint.
   - The known-flake test gets an isolated re-run path in the gate.
   - Pattern-match-on-null syntax (`if (Model is { } model)`) is suggested at every nullable channel-access site to avoid sprinkling `!` operators.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-05-18-phase-3b-consumerhost.md`. Subagent-driven execution recommended (same workflow as Phase 1, 2, 3a, 3c).
