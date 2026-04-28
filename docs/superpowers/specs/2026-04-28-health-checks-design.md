# Health Checks Packages, Producer-Connection Surface, and Website Updates

Date: 2026-04-28
Branch context: `v7-clean-architecture`

## Summary

Three coordinated changes:

1. **Framework** — promote `ProducerConnection.IsHealthy()` from `internal` to a public method on a new public interface `IProducerConnection` in `ServiceConnect.Client.RabbitMQ`. Mirrors the existing public surface of `IServiceConnectConnection.IsConnected()` on the consumer side.
2. **New packages** — add `ServiceConnect.HealthChecks` (transport-agnostic, surfaces bus consumption state) and `ServiceConnect.HealthChecks.RabbitMQ` (surfaces broker connection state for consumer and/or producer). Each package exposes idiomatic extension methods on `IHealthChecksBuilder`.
3. **Website** — replace the "## No health checks" section of [observability.mdx](website/src/content/docs/learn/operations/observability.mdx) with a "## Health checks" section that documents installation, wiring, and steady-state semantics. Add a short pointer paragraph from `hosting.mdx`.

## Motivation

The current docs steer users to roll their own:

> ServiceConnect does not ship an `IHealthCheck` implementation. […] If you need more than that, wrap `IBus.IsConsuming` in a custom `IHealthCheck` pointed at the container's bus.
> — [observability.mdx:138-140](website/src/content/docs/learn/operations/observability.mdx#L138-L140)

Two things have changed since that paragraph was written. First, the components every meaningful check would need are now in place and stable: [`IBus.IsConsuming`](src/ServiceConnect.Interfaces/Bus/IBus.cs#L79), [`IServiceConnectConnection.IsConnected()`](src/ServiceConnect.Client.RabbitMQ/Connection/IServiceConnectConnection.cs#L30), and [`ProducerConnection.IsHealthy()`](src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs#L78). Second, the precedent for shipping optional add-on packages alongside the bus is now established by `ServiceConnect.Telemetry`. There is no remaining reason to make every adopter re-implement the same two or three checks.

The user-facing goal is a two-line wire-up alongside their existing `AddServiceConnect` call:

```csharp
services.AddServiceConnect(builder =>
{
    builder.UseRabbitMQ(/* ... */);
});

services.AddHealthChecks()
    .AddServiceConnect(tags: new[] { "live" })
    .AddServiceConnectRabbitMQ(tags: new[] { "ready" });
```

…with the `AddServiceConnectRabbitMQ()` call adapting automatically to whether the host is configured for consume, publish, or both.

## Goals

- Three checks ship: bus liveness, RabbitMQ consumer-connection readiness, RabbitMQ producer-connection readiness.
- Idiomatic registration: extension methods on `IHealthChecksBuilder`, matching the shape of `AddNpgSql`, `AddRedis`, etc.
- The RabbitMQ extension method registers 0, 1, or 2 checks based on which connection components are in DI, so a publish-only or consume-only host gets the right thing automatically.
- All checks are O(1), allocation-light, and side-effect-free — they inspect last-known in-process state. No I/O, no AMQP round-trips.
- Transport-agnostic check (bus liveness) lives in a transport-agnostic package (`ServiceConnect.HealthChecks`); transport-specific checks live in the RabbitMQ-specific package (`ServiceConnect.HealthChecks.RabbitMQ`). The transport seam is preserved.
- `ProducerConnection.IsHealthy()` becomes part of a stable, public, minimal interface (`IProducerConnection`) without exposing other internals.
- Documentation in `observability.mdx` is rewritten to match shipped reality, with installation, wiring, semantics, and the publish-only/consume-only behaviour explained.

## Non-goals

- Active broker probing (no AMQP heartbeat injection, no synthetic publish, no test-channel open per probe). The shipped checks reflect last-known connection state from the RabbitMQ client's own event stream. Active probing is what we do not want, and we explicitly justify it (see *Steady-state semantics*).
- A second transport. The repo is RabbitMQ-only today; the package layout is forward-compatible with a future transport (e.g. `ServiceConnect.HealthChecks.Kafka`) but no second-transport package is in scope.
- Distinguishing "bus has not started yet" from "bus has been terminally stopped" inside the liveness check. `IBus` does not expose this distinction, and the right tool for the startup window is K8s `initialDelaySeconds` or ASP.NET endpoint ordering, not a bus-level state machine. (Discussed and ruled out.)
- An `examples/HealthChecks` example project. The wiring snippet in `observability.mdx` is enough; we can add a sample later if uptake suggests it is needed.
- README changes. The website is the canonical learn-path docs; rebalancing the README around health checks is out of scope.
- Changes to any other public interface in `ServiceConnect.Client.RabbitMQ` beyond the `IProducerConnection` promotion described above. In particular, `IServiceConnectConnection` is unchanged.
- A `Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheckPublisher` integration (the polling-with-callback pattern). Not needed for the minimum useful surface; users who want it can wire it up via the standard `IHealthChecksBuilder.AddCheck` registrations our packages produce.

## Design

### Project layout

Two new projects under `src/`:

```
src/ServiceConnect.HealthChecks/
    BusConsumingHealthCheck.cs
    HealthChecksBuilderExtensions.cs
    ServiceConnect.HealthChecks.csproj
    ServiceConnect.HealthChecks.nuspec

src/ServiceConnect.HealthChecks.RabbitMQ/
    RabbitMqConsumerConnectionHealthCheck.cs
    RabbitMqProducerConnectionHealthCheck.cs
    HealthChecksBuilderExtensions.cs
    ServiceConnect.HealthChecks.RabbitMQ.csproj
    ServiceConnect.HealthChecks.RabbitMQ.nuspec
```

Both projects are added to `src/ServiceConnect.slnx`. Both inherit TFMs from `Directory.Build.props` (no per-project TFM overrides), per the centralisation in commit `9b13f3ab`.

### Dependencies

**`ServiceConnect.HealthChecks.csproj`**:

- `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` — the *abstractions* package, not the full `Microsoft.Extensions.Diagnostics.HealthChecks`. The abstractions package contains `IHealthCheck`, `HealthCheckResult`, `HealthStatus`, `HealthCheckRegistration`, and the `IHealthChecksBuilder` interface — everything our extensions and check classes need to compile and run. The full package adds `IHealthCheckPublisher`, the hosted-service publisher implementation, and `services.AddHealthChecks()` itself; consuming applications already pull that in (it is a transitive of the `Microsoft.AspNetCore.Diagnostics.HealthChecks` integration they use to expose the `/health` endpoint). Depending on abstractions only keeps our packaging lean and avoids duplicating publisher/registration plumbing into the dependency closure.
- `ProjectReference` to `ServiceConnect.Interfaces`. **Not** to `ServiceConnect`. The check only needs `IBus`.

**`ServiceConnect.HealthChecks.RabbitMQ.csproj`**:

- `ProjectReference` to `ServiceConnect.HealthChecks` (transitively brings in the abstractions package and the `Interfaces` reference).
- `ProjectReference` to `ServiceConnect.Client.RabbitMQ` (the RabbitMQ-specific connection types).

### Surface change in `ServiceConnect.Client.RabbitMQ`

Promote `ProducerConnection.IsHealthy()` to public surface:

```csharp
namespace ServiceConnect.Client.RabbitMQ;

public interface IProducerConnection
{
    bool IsHealthy();
}
```

`ProducerConnection` (which already exists and is what is registered in DI today) implements `IProducerConnection`. The DI registration changes from `AddSingleton<ProducerConnection>` to `AddSingleton<IProducerConnection, ProducerConnection>`, plus — if any internal consumer of the concrete type still needs it directly — a `services.AddSingleton(sp => (ProducerConnection)sp.GetRequiredService<IProducerConnection>())` shim. The implementation work will check whether any internal call site requires the concrete type and either keep that shim or change the call site to depend on the interface; either is acceptable.

The interface is deliberately minimal (one method). It is *not* a publish-side counterpart to `IServiceConnectConnection` — it is just the slice of `ProducerConnection` that has to be visible from outside the assembly to support a health check. Anything else stays internal.

This is the same shape of small public-surface promotion done for `MessageTypeExchangeName` in commit `5f87d039`.

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

public static class HealthChecksBuilderExtensions
{
    public static IHealthChecksBuilder AddServiceConnect(
        this IHealthChecksBuilder builder,
        string name = "serviceconnect-bus",
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null);
}
```

`AddServiceConnect` registers a single `HealthCheckRegistration` whose factory resolves `BusConsumingHealthCheck` from the `IServiceProvider` (which in turn resolves `IBus`). Tags, failure status, and timeout flow through to the registration verbatim. The implementation lives in a few dozen lines.

### Public API of `ServiceConnect.HealthChecks.RabbitMQ`

```csharp
namespace ServiceConnect.HealthChecks.RabbitMQ;

public sealed class RabbitMqConsumerConnectionHealthCheck : IHealthCheck
{
    public RabbitMqConsumerConnectionHealthCheck(IServiceConnectConnection connection);
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default);
}

public sealed class RabbitMqProducerConnectionHealthCheck : IHealthCheck
{
    public RabbitMqProducerConnectionHealthCheck(IProducerConnection connection);
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default);
}

public static class HealthChecksBuilderExtensions
{
    public static IHealthChecksBuilder AddServiceConnectRabbitMQ(
        this IHealthChecksBuilder builder,
        string consumerName = "serviceconnect-rabbitmq-consumer",
        string producerName = "serviceconnect-rabbitmq-producer",
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null);
}
```

`AddServiceConnectRabbitMQ` uses DI introspection to decide what to register:

1. Read `builder.Services` (the underlying `IServiceCollection`).
2. If a registration for `IServiceConnectConnection` exists, register `RabbitMqConsumerConnectionHealthCheck` under `consumerName`.
3. If a registration for `IProducerConnection` exists, register `RabbitMqProducerConnectionHealthCheck` under `producerName`.
4. If neither registration exists, the call is a silent no-op. We do not throw (which would penalise composition order — `AddHealthChecks` before `UseRabbitMQ`) and we do not log (no `ILogger` is available at `IHealthChecksBuilder` extension time without building a temporary `IServiceProvider`, which is an unwarranted side-effect for a registration call). The diagnostic signal is the empty `/health/ready` endpoint at runtime, which is the same signal every other ecosystem health-checks library exposes for the same misconfiguration. The docs name this case explicitly so the on-call engineer knows what to look for.

The introspection is a `services.Any(d => d.ServiceType == typeof(...))` check at registration time. It is not a runtime introspection — by the time `AddServiceConnectRabbitMQ` is called, `services.AddServiceConnect(...).UseRabbitMQ(...)` has already produced its `ServiceDescriptor` entries.

Tags, failure status, and timeout are applied to *every* check the call registers. Per-check tags are intentionally not supported — the K8s probe model treats consumer and producer readiness as a single "ready to do RabbitMQ work" concern and tagging them identically is the right default. A user who needs different tags per check can call `AddCheck<T>` directly with the public health-check classes (which is the standard escape hatch).

### Steady-state semantics

| Check | Healthy when | Unhealthy when | Description string |
|---|---|---|---|
| `BusConsumingHealthCheck` | `IBus.IsConsuming == true` | `IBus.IsConsuming == false` | `"Bus is consuming."` / `"Bus is not consuming."` |
| `RabbitMqConsumerConnectionHealthCheck` | `IServiceConnectConnection.IsConnected() == true` | `IServiceConnectConnection.IsConnected() == false` | `"RabbitMQ consumer connection is open."` / `"RabbitMQ consumer connection is closed."` |
| `RabbitMqProducerConnectionHealthCheck` | `IProducerConnection.IsHealthy() == true` | `IProducerConnection.IsHealthy() == false` | `"RabbitMQ producer connection is open."` / `"RabbitMQ producer connection is closed."` |

All three checks are synchronous in body and return `Task.FromResult<HealthCheckResult>` — there is no `async`/`await`, no I/O, no allocations beyond the `HealthCheckResult` struct construction. `cancellationToken` is honoured trivially (an inspection that takes no time cannot meaningfully cancel mid-flight) but it is accepted on the signature for `IHealthCheck` conformance.

Why no active probing: `AspNetCore.HealthChecks.RabbitMQ`'s common pattern is to open a channel per probe, which competes with real traffic and amplifies failure modes (a probe interval of 5s × N replicas ≈ steady channel churn against the broker). The RabbitMQ client we use already maintains an event-driven view of the connection state — `IsOpen` and the equivalent flags reflect the most recent shutdown / blocked / unblocked event. Surfacing that view is honest, costs nothing, and is what production-quality clients have done for years (the official RabbitMQ .NET examples use the same model).

A consequence to call out: a Healthy result means *the last known state of the connection was open*. If the connection has just dropped and the client has not yet processed the shutdown event, there is a small (millisecond-scale) window during which a probe can still report Healthy. This window is dwarfed by the K8s probe interval and — more importantly — is the same window that any in-process check has, regardless of implementation. We document this in `observability.mdx`.

### Wiring example (for the docs)

```csharp
using ServiceConnect.HealthChecks;
using ServiceConnect.HealthChecks.RabbitMQ;

services.AddServiceConnect(builder =>
{
    builder.UseRabbitMQ(opts => opts.ConnectionString = "amqp://...");
});

services.AddHealthChecks()
    .AddServiceConnect(tags: new[] { "live" })
    .AddServiceConnectRabbitMQ(tags: new[] { "ready" });

// In Program.cs / Startup.cs:
app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
```

A publish-only host (no `AddConsumer<T>` registrations, so no `IServiceConnectConnection` in DI) gets the producer check on `/health/ready` and nothing else under that tag. A consume-only host (no producer wiring) gets the consumer check on `/health/ready` and nothing else under that tag. A host with neither produces a perpetually-empty `/health/ready` — that is the visible signal that the user called `AddServiceConnectRabbitMQ` before `UseRabbitMQ`, or that the RabbitMQ transport is not wired at all.

## Testing

### Unit tests (`src/ServiceConnect.UnitTests/HealthChecks/`)

- `BusConsumingHealthCheckTests.cs`
  - Returns Healthy when `IBus.IsConsuming == true`.
  - Returns Unhealthy when `IBus.IsConsuming == false`.
  - Honours caller-supplied `failureStatus` (`Degraded` flows through to the result, not `Unhealthy`).
  - `Description` contains the expected text. Assertion is on a substring, not the whole string, so cosmetic copy edits do not break tests.
- `RabbitMqConsumerConnectionHealthCheckTests.cs` — mirror of the above against `IServiceConnectConnection`.
- `RabbitMqProducerConnectionHealthCheckTests.cs` — mirror of the above against `IProducerConnection`.
- `HealthChecksBuilderExtensionsTests.cs` (core)
  - `AddServiceConnect` registers a single `HealthCheckRegistration` with the configured name, tags, timeout, and `failureStatus`.
  - Default name is `"serviceconnect-bus"`.
- `RabbitMqHealthChecksBuilderExtensionsTests.cs`
  - Both checks registered when `IServiceConnectConnection` and `IProducerConnection` are in DI.
  - Only the consumer check registered when only `IServiceConnectConnection` is in DI.
  - Only the producer check registered when only `IProducerConnection` is in DI.
  - No checks registered when neither is in DI (silent no-op).
  - Custom `consumerName` / `producerName` propagate through to the registrations.

### Integration test (`src/ServiceConnect.EndToEndTests/HealthChecks/`)

One file: `HealthCheckEndToEndTests.cs`. One test method that exercises wiring against the existing Testcontainers RabbitMQ:

1. Build a host with `services.AddServiceConnect(...).UseRabbitMQ(...)` plus `services.AddHealthChecks().AddServiceConnect().AddServiceConnectRabbitMQ()`.
2. Start the host. Assert all three checks report Healthy via `HealthCheckService.CheckHealthAsync`.
3. Drop the broker connection (using whichever helper the existing E2E suite already uses to simulate broker outages — the implementation step locates this).
4. Assert the two RabbitMQ checks flip to Unhealthy. The bus-consuming check stays Healthy: the bus is still notionally consuming and the RabbitMQ client is reconnecting under it. This is the *intended* shape — liveness ("the host is alive and trying") and readiness ("the broker is reachable right now") are different signals.
5. Bring the connection back. Assert all three return to Healthy.

The test asserts steady states only — never transitions. We do not control the order in which the RabbitMQ client surfaces shutdown / reconnect events to the consumer vs producer connections, and asserting transition order would be flaky.

No additional E2E tests for publish-only or consume-only topologies. The `RabbitMqHealthChecksBuilderExtensionsTests` unit suite covers the registration-time decision tree, and adding broker-bound E2E variants of the same logic is duplication.

### Existing tests

`BusHostedServiceTests` and `BusLifecycleTests` already cover the bus-state surface that the liveness check observes. No additions there.

## Documentation

### Rewrite `## No health checks` → `## Health checks` in `observability.mdx`

[website/src/content/docs/learn/operations/observability.mdx:136-140](website/src/content/docs/learn/operations/observability.mdx#L136-L140) currently reads:

> ServiceConnect does not ship an `IHealthCheck` implementation. […] If you need more than that, wrap `IBus.IsConsuming` in a custom `IHealthCheck` pointed at the container's bus.

The replacement section is structured as:

1. **One-paragraph framing.** Three checks ship: bus liveness, RabbitMQ consumer-connection readiness, RabbitMQ producer-connection readiness. The first ships in `ServiceConnect.HealthChecks`; the latter two ship in `ServiceConnect.HealthChecks.RabbitMQ`.
2. **Install:**
   ```bash
   dotnet add package ServiceConnect.HealthChecks
   dotnet add package ServiceConnect.HealthChecks.RabbitMQ
   ```
3. **Wiring:** the snippet from *Wiring example* above, including `MapHealthChecks` predicates.
4. **Per-check semantics table** mirroring the *Steady-state semantics* table above.
5. **Publish-only / consume-only paragraph:** which checks are registered in which topology, and that an empty `/health/ready` endpoint means `AddServiceConnectRabbitMQ` was called before `UseRabbitMQ` (or there is no RabbitMQ wiring at all).
6. **Steady-state caveat:** "Healthy means *the last known state of the connection was open*. We surface in-memory state from the RabbitMQ client's event stream rather than opening a probe channel per call. The cost of the alternative — channel churn against the broker on every probe interval — is not worth the marginal freshness."
7. **Cross-link** to `Configuration` and `Hosting`, matching the page's existing "What comes next" discipline.

### Add pointer in `hosting.mdx`

One short paragraph (one or two sentences) under the most natural anchor in [hosting.mdx](website/src/content/docs/learn/operations/hosting.mdx), pointing readers to the new health-checks section. The exact anchor will be picked during implementation by reading the file. The pointer keeps the canonical reference in `observability.mdx` and avoids duplicating wiring snippets.

### No `examples/HealthChecks` project

Out of scope. The wiring snippet in `observability.mdx` is sufficient.

## Implementation order

1. Promote `ProducerConnection.IsHealthy()` to `IProducerConnection.IsHealthy()` in `ServiceConnect.Client.RabbitMQ`. Update DI registration. Confirm internal callers either move to the interface or are unaffected.
2. Add `ServiceConnect.HealthChecks` project: csproj, nuspec, `BusConsumingHealthCheck`, `HealthChecksBuilderExtensions.AddServiceConnect`. Add to `slnx`.
3. Add `ServiceConnect.HealthChecks.RabbitMQ` project: csproj, nuspec, two checks, `HealthChecksBuilderExtensions.AddServiceConnectRabbitMQ` with DI introspection. Add to `slnx`.
4. Unit tests in `ServiceConnect.UnitTests/HealthChecks/` for all three checks and both extension methods.
5. Integration test in `ServiceConnect.EndToEndTests/HealthChecks/HealthCheckEndToEndTests.cs`.
6. Rewrite the observability doc section. Add the `hosting.mdx` pointer.
7. Verify packaging: `dotnet pack` produces both `.nupkg`s with the expected dependency closure (abstractions only on the core package, RabbitMQ client on the transport package).

Each step is independently revertable. Step 1 is a small `internal → public` promotion that can ship on its own; steps 2–7 build on it.

## Risks and open questions

- **DI introspection robustness.** The `AddServiceConnectRabbitMQ` shape relies on `IServiceCollection.Any(d => d.ServiceType == typeof(...))` reflecting the registrations made by `UseRabbitMQ`. If a future refactor replaces named-singleton registration with keyed services or a different binding style, the introspection has to track. This is a maintenance contract worth a one-line note in the RabbitMQ extensions class.
- **`IProducerConnection` interface naming.** The name is generic enough that a future second transport could collide on it. The interface lives in the `ServiceConnect.Client.RabbitMQ` namespace, so technically there is no clash, but if a future `Kafka` transport invents its own `IProducerConnection` we will have an unfortunate symmetry. Acceptable for now — the namespace differentiates — but worth flagging.
- **Health check timeout default.** We pass `timeout: null` through to `HealthCheckRegistration`, which means "no timeout". Since our checks are O(1) state inspections, no timeout is fine — but a hostile DI registration could substitute a slow `IBus` mock that hangs `IsConsuming`. We accept this; it is the user's problem.
- **Behaviour when `BusHostedService.StartAsync` has not yet completed.** In the `AutoStartConsuming = true` path, `IsConsuming` is `false` until `StartAsync` finishes. ASP.NET does not begin serving requests (and therefore does not poll `MapHealthChecks` endpoints) until startup completes, so under the standard wiring this window is invisible to probes. If a user takes an unusual setup — e.g. polling `HealthCheckService` from a different host — they will see the transient `false`. This is documented in the steady-state caveat paragraph.
