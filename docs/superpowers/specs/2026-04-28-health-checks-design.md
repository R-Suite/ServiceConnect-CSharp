# Health Checks Package, IProducer Surface, and Website Updates

Date: 2026-04-28
Branch context: `v7-clean-architecture`

## Summary

Three coordinated changes:

1. **Framework** — add `bool IsHealthy { get; }` to `ServiceConnect.Interfaces.IProducer`, mirroring the existing `IConsumer.IsConnected` property. The RabbitMQ `Producer` implementation delegates to its private `ProducerConnection.IsHealthy()`. No new interface; one extra member on an existing transport-agnostic interface.
2. **New package** — add `ServiceConnect.HealthChecks` with three opt-in `IHealthChecksBuilder` extension methods (`AddServiceConnectBus`, `AddServiceConnectConsumer`, `AddServiceConnectProducer`) and three matching `IHealthCheck` classes. The package depends only on `ServiceConnect.Interfaces` and `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions`. It contains no transport-specific code.
3. **Website** — replace the "## No health checks" section of [observability.mdx](website/src/content/docs/learn/operations/observability.mdx) with a "## Health checks" section that documents installation, the three opt-in calls, the matching K8s liveness/readiness story, and the steady-state semantics. Add a short pointer paragraph from `hosting.mdx`.

## Motivation

The current docs steer users to roll their own:

> ServiceConnect does not ship an `IHealthCheck` implementation. […] If you need more than that, wrap `IBus.IsConsuming` in a custom `IHealthCheck` pointed at the container's bus.
> — [observability.mdx:138-140](website/src/content/docs/learn/operations/observability.mdx#L138-L140)

Two things have changed since that paragraph was written. First, the components every meaningful check would need are now stable and *transport-agnostic*: [`IBus.IsConsuming`](src/ServiceConnect.Interfaces/Bus/IBus.cs#L79) reports bus state, [`IConsumer.IsConnected`](src/ServiceConnect.Interfaces/Bus/IConsumer.cs) reports consumer-side connection state, and the symmetrical reading on the producer side already exists internally as [`ProducerConnection.IsHealthy()`](src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs#L78) — it just needs one extra method on `IProducer` to surface it. Second, the precedent for shipping optional add-on packages alongside the bus is now established by `ServiceConnect.Telemetry`. There is no remaining reason to make every adopter re-implement the same two or three checks.

The user-facing goal is opt-in wiring matched to the host's actual workload:

```csharp
services.AddServiceConnect(builder =>
{
    builder.UseRabbitMQ(/* ... */);
});

services.AddHealthChecks()
    .AddServiceConnectBus(tags: new[] { "live" })
    .AddServiceConnectConsumer(tags: new[] { "ready" })   // omit on publish-only hosts
    .AddServiceConnectProducer(tags: new[] { "ready" });  // omit on consume-only hosts
```

## Goals

- Three opt-in `IHealthCheck` classes ship in one package, each registered through its own `IHealthChecksBuilder` extension method. The user picks the calls that match their topology.
- The package is transport-agnostic: it depends only on `ServiceConnect.Interfaces`, not on `ServiceConnect.Client.RabbitMQ`. A future `Kafka` transport that implements the same interfaces gets the same checks for free.
- All checks are O(1), allocation-light, and side-effect-free — they inspect last-known in-process state through public interface members. No I/O, no AMQP round-trips, no broker channels opened per probe.
- `IProducer` gains exactly one member (`bool IsHealthy { get; }`) — the smallest change needed to mirror `IConsumer.IsConnected`.
- Documentation in `observability.mdx` is rewritten to match shipped reality: installation, the three opt-in calls, K8s wiring, the publish-only / consume-only / both topologies, and the "before first publish" semantic for the producer check.

## Non-goals

- Active broker probing (no AMQP heartbeat injection, no synthetic publish, no test-channel open per probe). The shipped checks reflect last-known connection state from the transport client's own event stream.
- A second transport. The repo is RabbitMQ-only today; the package layout is forward-compatible (any transport whose `IConsumer`/`IProducer` implementations honour `IsConnected`/`IsHealthy` correctly works with these checks).
- A `ServiceConnect.HealthChecks.RabbitMQ` second package. Considered and rejected: there is no transport-specific code in the checks themselves, so a second package would be empty packaging, not a real seam.
- DI introspection at registration time ("only register the producer check if a producer is in DI"). Considered and rejected: `UseRabbitMQ` always registers both `IProducer` and `IConsumer` regardless of whether the host actually publishes or consumes, so DI presence is not a useful signal. Explicit per-method opt-in puts the decision where it belongs — with the user, who knows their topology.
- Distinguishing "bus has not started yet" from "bus has been terminally stopped" inside the liveness check. `IBus` does not expose this distinction, and the right tool for the startup window is K8s `initialDelaySeconds` or ASP.NET endpoint ordering, not a bus-level state machine.
- A new public interface in `ServiceConnect.Client.RabbitMQ` (e.g. `IProducerConnection`). The right surface promotion is on `IProducer` itself, in `ServiceConnect.Interfaces`. The connection-layer types stay `internal`.
- An `examples/HealthChecks` example project. The wiring snippet in `observability.mdx` is enough.
- README changes. The website is the canonical learn-path docs.
- A `Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheckPublisher` integration. Users who want one wire it up against the standard `IHealthChecksBuilder` registrations our methods produce.

## Design

### Project layout

One new project under `src/`:

```
src/ServiceConnect.HealthChecks/
    BusConsumingHealthCheck.cs
    ConsumerConnectionHealthCheck.cs
    ProducerConnectionHealthCheck.cs
    HealthChecksBuilderExtensions.cs
    ServiceConnect.HealthChecks.csproj
    ServiceConnect.HealthChecks.nuspec
```

Added to `src/ServiceConnect.slnx`. Inherits TFMs from `Directory.Build.props` (no per-project TFM overrides), per the centralisation in commit `9b13f3ab`.

### Dependencies

**`ServiceConnect.HealthChecks.csproj`**:

- `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` — the *abstractions* package only. Contains `IHealthCheck`, `HealthCheckResult`, `HealthStatus`, `HealthCheckRegistration`, and `IHealthChecksBuilder` — everything the extensions and check classes need to compile and run. The full `Microsoft.Extensions.Diagnostics.HealthChecks` package adds publisher/registration plumbing that consuming applications already pull in via `Microsoft.AspNetCore.Diagnostics.HealthChecks`.
- `ProjectReference` to `ServiceConnect.Interfaces`. **Not** to `ServiceConnect`, **not** to `ServiceConnect.Client.RabbitMQ`. The checks resolve `IBus`, `IConsumer`, `IProducer` — all of which live in `Interfaces`.

### Surface change in `ServiceConnect.Interfaces`

Add one property to `IProducer` (in [src/ServiceConnect.Interfaces/Bus/IProducer.cs](src/ServiceConnect.Interfaces/Bus/IProducer.cs)):

```csharp
/// <summary>
/// Gets whether the producer is currently connected and ready to publish or send.
/// </summary>
/// <remarks>
/// Returns <see langword="false"/> before the first publish/send call (the producer
/// connects lazily) and after a connection drop until the next reconnect. Mirrors
/// <see cref="IConsumer.IsConnected"/>.
/// </remarks>
bool IsHealthy { get; }
```

Implementation in [src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs](src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs):

```csharp
public bool IsHealthy => _producerConnection.IsHealthy();
```

`ProducerConnection.IsHealthy()` itself stays `internal`. The connection-layer types are not promoted; only the `IProducer` surface gains a member.

This is a smaller change than the original spec proposed (which invented an `IProducerConnection` in the RabbitMQ assembly). It puts the readable surface where it belongs: alongside `IConsumer.IsConnected`, in the same transport-agnostic interfaces project, with symmetrical naming.

### Public API of `ServiceConnect.HealthChecks`

```csharp
namespace ServiceConnect.HealthChecks;

public sealed class BusConsumingHealthCheck : IHealthCheck
{
    public BusConsumingHealthCheck(IBus bus);
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default);
}

public sealed class ConsumerConnectionHealthCheck : IHealthCheck
{
    public ConsumerConnectionHealthCheck(IConsumer consumer);
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default);
}

public sealed class ProducerConnectionHealthCheck : IHealthCheck
{
    public ProducerConnectionHealthCheck(IProducer producer);
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default);
}

public static class HealthChecksBuilderExtensions
{
    public static IHealthChecksBuilder AddServiceConnectBus(
        this IHealthChecksBuilder builder,
        string name = "serviceconnect-bus",
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null);

    public static IHealthChecksBuilder AddServiceConnectConsumer(
        this IHealthChecksBuilder builder,
        string name = "serviceconnect-consumer",
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null);

    public static IHealthChecksBuilder AddServiceConnectProducer(
        this IHealthChecksBuilder builder,
        string name = "serviceconnect-producer",
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null);
}
```

Each method registers exactly one `HealthCheckRegistration`. Tags, failure status, name, and timeout flow through verbatim. No DI introspection. The user calls only the methods that match what their host actually does.

### Steady-state semantics

| Check | Healthy when | Unhealthy when | Description string |
|---|---|---|---|
| `BusConsumingHealthCheck` | `IBus.IsConsuming == true` | `IBus.IsConsuming == false` | `"Bus is consuming."` / `"Bus is not consuming."` |
| `ConsumerConnectionHealthCheck` | `IConsumer.IsConnected == true` | `IConsumer.IsConnected == false` | `"Consumer connection is open."` / `"Consumer connection is closed."` |
| `ProducerConnectionHealthCheck` | `IProducer.IsHealthy == true` | `IProducer.IsHealthy == false` | `"Producer connection is open."` / `"Producer connection is closed."` |

All three checks are synchronous in body and return `Task.FromResult<HealthCheckResult>` — no `async`/`await`, no I/O, no allocations beyond the `HealthCheckResult` struct construction. `cancellationToken` is honoured trivially (an inspection that takes no time cannot meaningfully cancel mid-flight) but is accepted on the signature for `IHealthCheck` conformance.

**Two timing footnotes worth documenting** so on-call engineers do not chase ghosts:

1. **Producer "before first publish"**: the RabbitMQ `Producer` connects lazily — the first call to `PublishAsync`/`SendAsync` triggers `EnsureConnectedAsync`. Until that first call, `IsHealthy` is `false` and the producer check reports Unhealthy. For hosts that publish anything at startup (most do — request/reply, heartbeats, subscribe-confirm) this resolves within milliseconds. For hosts that publish only in response to inbound messages, the producer check is Unhealthy until the first outbound message. This is intentional: a producer that has never connected is genuinely not ready, and pretending otherwise hides a real failure mode (broker unreachable from startup).
2. **Last-known state, not active probe**: a Healthy result means *the last shutdown event observed by the transport client had not yet fired*. If a connection has just dropped and the client has not yet processed the shutdown event, there is a small (millisecond-scale) window during which a probe can still report Healthy. This window is dwarfed by the K8s probe interval and is the same window any in-process check has, regardless of implementation. The alternative — opening a probe channel per call — costs a per-replica-per-interval channel against the broker, which is not worth the marginal freshness.

### Wiring example (for the docs)

```csharp
using ServiceConnect.HealthChecks;

services.AddServiceConnect(builder =>
{
    builder.UseRabbitMQ(opts => opts.ConnectionString = "amqp://...");
});

services.AddHealthChecks()
    .AddServiceConnectBus(tags: new[] { "live" })
    .AddServiceConnectConsumer(tags: new[] { "ready" })
    .AddServiceConnectProducer(tags: new[] { "ready" });

// In Program.cs / Startup.cs:
app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
```

For a publish-only host, drop the `AddServiceConnectConsumer` line. For a consume-only host, drop `AddServiceConnectProducer`. The bus liveness check is always meaningful as long as the bus is in the host (it reports `false` until the bus has started consuming, which `BusHostedService.StartAsync` blocks on at host startup).

## Testing

### Unit tests (`src/ServiceConnect.UnitTests/HealthChecks/`)

- `BusConsumingHealthCheckTests.cs`
  - Returns Healthy when `IBus.IsConsuming == true`.
  - Returns Unhealthy when `IBus.IsConsuming == false`.
  - Honours caller-supplied `failureStatus` (`Degraded` flows through to the result, not `Unhealthy`).
  - `Description` contains the expected text. Assertion on a substring, not the whole string.
- `ConsumerConnectionHealthCheckTests.cs` — mirror of the above against `IConsumer.IsConnected`.
- `ProducerConnectionHealthCheckTests.cs` — mirror of the above against `IProducer.IsHealthy`.
- `HealthChecksBuilderExtensionsTests.cs`
  - `AddServiceConnectBus` registers a `HealthCheckRegistration` with the configured name, tags, timeout, and `failureStatus`.
  - Default name is `"serviceconnect-bus"`.
  - `AddServiceConnectConsumer` — same shape, default name `"serviceconnect-consumer"`.
  - `AddServiceConnectProducer` — same shape, default name `"serviceconnect-producer"`.
  - Custom names propagate through.
  - `failureStatus = Degraded` makes the registration's `FailureStatus` return Degraded.

### Integration test (`src/ServiceConnect.EndToEndTests/HealthChecks/HealthCheckEndToEndTests.cs`)

One test method that exercises wiring against the existing Testcontainers RabbitMQ:

1. Build a host with `services.AddServiceConnect(...).UseRabbitMQ(...)` plus all three `AddServiceConnect…` calls.
2. Trigger a publish at startup (so the producer connects). Assert all three checks report Healthy via `HealthCheckService.CheckHealthAsync`.
3. Drop the broker connection (using whichever helper the existing E2E suite already uses to simulate broker outages — the implementation step locates this).
4. Assert the consumer and producer checks flip to Unhealthy. The bus-consuming check stays Healthy: the bus is still notionally consuming, the transport client is reconnecting under it. This is the *intended* shape — liveness ("the host is alive and trying") and readiness ("the broker is reachable right now") are different signals.
5. Bring the connection back. Trigger another publish (so the producer reconnects, since reconnection is also lazy on the first publish-after-drop). Assert all three return to Healthy.

The test asserts steady states only — never transitions. We do not control the order in which the transport client surfaces shutdown / reconnect events to the consumer vs producer connections.

No additional E2E tests for publish-only or consume-only topologies. The unit tests cover each `IHealthCheck`'s observation of its own interface independently, and adding broker-bound E2E variants is duplication.

### Existing tests

`BusHostedServiceTests` and `BusLifecycleTests` already cover the bus-state surface that the liveness check observes. No additions there. `IConsumer.IsConnected` is tested in the consumer suite. The new `IProducer.IsHealthy` member needs at least one unit test in the producer suite confirming it delegates to `_producerConnection.IsHealthy()` (the spec's surface promotion is small enough that the dedicated unit test in `ProducerConnectionHealthCheckTests` arguably covers it via the mock, but a one-line `Producer`-side test that the property delegates is cheap insurance and matches the existing test style).

## Documentation

### Rewrite `## No health checks` → `## Health checks` in `observability.mdx`

[website/src/content/docs/learn/operations/observability.mdx:136-140](website/src/content/docs/learn/operations/observability.mdx#L136-L140) currently reads:

> ServiceConnect does not ship an `IHealthCheck` implementation. […] If you need more than that, wrap `IBus.IsConsuming` in a custom `IHealthCheck` pointed at the container's bus.

The replacement section is structured as:

1. **One-paragraph framing.** Three opt-in `IHealthCheck` classes ship in `ServiceConnect.HealthChecks`. They observe `IBus`, `IConsumer`, `IProducer` — all transport-agnostic.
2. **Install:**
   ```bash
   dotnet add package ServiceConnect.HealthChecks
   ```
3. **Wiring:** the snippet from *Wiring example* above, including `MapHealthChecks` predicates.
4. **Per-check semantics table** mirroring the *Steady-state semantics* table above.
5. **Topology paragraph:** "Pick the calls that match what your host does. A consume-only host omits `AddServiceConnectProducer`. A publish-only host omits `AddServiceConnectConsumer`. Hosts that do both call all three."
6. **Producer-before-first-publish caveat:** quoted from *Steady-state semantics* footnote 1.
7. **Steady-state caveat:** quoted from *Steady-state semantics* footnote 2.
8. **Cross-link** to `Configuration` and `Hosting`, matching the page's existing "What comes next" discipline.

### Add pointer in `hosting.mdx`

One short paragraph (one or two sentences) under the most natural anchor in [hosting.mdx](website/src/content/docs/learn/operations/hosting.mdx), pointing readers to the new health-checks section. The exact anchor will be picked during implementation by reading the file. The pointer keeps the canonical reference in `observability.mdx` and avoids duplicating wiring snippets.

### No `examples/HealthChecks` project

Out of scope.

## Implementation order

1. **`IProducer.IsHealthy` promotion.** Add the property to the interface; implement on `Producer` as a delegating computed property; one-line unit test on `Producer` confirming delegation. Independently committable and revertable.
2. **`ServiceConnect.HealthChecks` project skeleton.** csproj, nuspec, add to `slnx`. Empty implementation files, no checks yet — confirms the build, packaging, and slnx wiring are correct in isolation.
3. **`BusConsumingHealthCheck` + `AddServiceConnectBus`** + unit tests for both.
4. **`ConsumerConnectionHealthCheck` + `AddServiceConnectConsumer`** + unit tests for both.
5. **`ProducerConnectionHealthCheck` + `AddServiceConnectProducer`** + unit tests for both.
6. **`HealthChecksBuilderExtensionsTests` registration tests** — name, tags, timeout, failureStatus propagation across all three methods.
7. **Integration test** at `ServiceConnect.EndToEndTests/HealthChecks/HealthCheckEndToEndTests.cs`.
8. **Rewrite the observability doc section.** Add the `hosting.mdx` pointer.
9. **Verify packaging:** `dotnet pack` produces `ServiceConnect.HealthChecks.nupkg` with the abstractions package as its only NuGet dependency and `ServiceConnect.Interfaces` as a project reference (which becomes the right NuGet dependency at pack time).

Each step is independently committable and revertable. Step 1 is a small interface promotion that ships on its own; steps 2–9 build on it.

## Risks and open questions

- **`IProducer.IsHealthy` semantics before first publish.** Documented above and in the docs section. The trade-off is between hiding a real "broker unreachable from startup" failure mode (which we reject) and reporting Unhealthy for hosts that don't publish at startup. K8s `initialDelaySeconds` typically masks the brief window for hosts that *do* publish at startup; hosts that genuinely publish only on inbound traffic should not register the producer check on a `ready` tag.
- **Health check timeout default.** We pass `timeout: null` through to `HealthCheckRegistration`, which means "no timeout". Since our checks are O(1) state inspections, no timeout is fine — but a hostile DI registration could substitute a slow `IBus`/`IConsumer`/`IProducer` mock that hangs the property getter. We accept this; it is the user's problem.
- **Behaviour when `BusHostedService.StartAsync` has not yet completed.** In the `AutoStartConsuming = true` path, `IsConsuming` is `false` until `StartAsync` finishes. ASP.NET does not begin serving requests until startup completes, so under the standard wiring this window is invisible to probes. Documented in the steady-state caveat paragraph.
- **`IConsumer.IsConnected` already exists; we do not add a redundant member.** The spec relies on this being the canonical surface for consumer connection state. If a future refactor moves connection state off `IConsumer`, the consumer check has to track. The check class is one method long, so this is a small maintenance contract.
