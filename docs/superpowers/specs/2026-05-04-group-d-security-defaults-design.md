# Group D — Security defaults — design

**Status:** approved, awaiting implementation plan
**Source review:** [architecture-review-deep.md](../../../architecture-review-deep.md)
**Roadmap:** [architecture-fix-plan.md](../../../architecture-fix-plan.md) — Group D
**Date:** 2026-05-04

---

## Overview

Group D makes ServiceConnect's transport security secure-by-default for v8. Three independent code/config items plus one doc carry-over from Group C, all under one Group:

1. **Item 2 (the strategic call):** Flip `TransportConfiguration.SslEnabled` default from `false` to `true`. **Breaking change for v8.** Every existing deployment that relies on plaintext must opt in by setting `SslEnabled = false`.
2. **Item 1:** Startup `Warning` log when `SslEnabled == false` and `Host` is non-loopback. Fires regardless of how `SslEnabled` got to false (no "default vs explicit" distinction). Filterable via standard `ILogger` category config.
3. **Item 3:** Verify RabbitMQ.Client v7's outstanding-publisher-confirmation tracker is bounded. If unbounded by default, set a sensible cap; if bounded already, document the bound.
4. **Item 6 (carry-over from Group C):** Extend `learn/operations/configuration.mdx#tls` and `releases.mdx` v8 highlights with the new TLS posture, port 5671 default, and how to opt out for local dev.

The strategic decision — whether to flip the `SslEnabled` default — was made with the user during brainstorm. The brief: v8 is the major-version window where breaking config changes are acceptable; aligning with secure-by-default norms wins over the localhost-dev convenience the v7 default optimised for. The runtime warning catches the case where an operator opts out of TLS in production by mistake. Localhost dev still works after one explicit `SslEnabled = false` opt-out per project — a one-line ceremony that the migration narrative documents.

### Cross-cutting decisions banked from brainstorm

- **Clean break.** No `[Obsolete]` shims, no transitional helpers, no `RequireTransportSecurity` strict-mode escalation flag. The flip plus the warning is the security posture.
- **No environment-aware suppression** of the warning. Standard `ILogger` category filtering is the escape hatch (raise `ServiceConnect.Client.RabbitMQ` to `Error` for environments where plaintext is intentional).
- **No DNS resolution for loopback detection** (TOCTOU + slow + complicates testing). Hostname-string check + `IPAddress.IsLoopback` for parsable IPs.
- **Item 3 is investigation-first.** If RabbitMQ.Client v7 already bounds the tracker by some other mechanism the implementation surface didn't surface, that commit becomes documentation-only instead of code. The plan accommodates either.

---

## In scope

| # | Change | Where | Driver |
|---|---|---|---|
| 2a | `TransportConfiguration.SslEnabled` initialiser changed from `false` to `true` | [src/ServiceConnect/Configuration/TransportConfiguration.cs:41](../../../src/ServiceConnect/Configuration/TransportConfiguration.cs#L41) | Secure-by-default |
| 2b | XML doc on `SslEnabled` rewritten — names new v8 default + opt-out path | Same file + [ITransportConfiguration.cs:51](../../../src/ServiceConnect.Interfaces/Configuration/ITransportConfiguration.cs#L51) | Documentation accuracy |
| 2c | Every example sets `SslEnabled = false` explicitly with a "local-dev plaintext; production must use TLS" comment | All 13 example projects under `examples/*` that have a `UseRabbitMQ(transport => …)` block | Examples must run unchanged against the local Docker compose |
| 2d | Every E2E test fixture sets `SslEnabled = false` | `src/ServiceConnect.EndToEndTests/**/*.cs` files containing `ConfigureTransport` | Testcontainers RabbitMQ runs plaintext |
| 2e | Unit tests asserting the default value updated | `src/ServiceConnect.UnitTests/TransportConfigurationTests.cs` | Reflect new default |
| 1a | New source-gen logger `RabbitMqClientLog` with `PlaintextOnNonLoopbackHost` event (id 1, level Warning) | Create: `src/ServiceConnect.Client.RabbitMQ/RabbitMqClientLog.cs` | Loud-but-filterable signal |
| 1b | Warning emitted from `ConnectionFactoryBuilder.Build` when `SslEnabled == false` and `Host` parses to a non-loopback address (or is a non-`localhost` hostname) | [src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs](../../../src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs) | Catches the production-misconfig case |
| 1c | Five-case unit test (loopback+plaintext, loopback+TLS, non-loopback+plaintext, non-loopback+TLS, comma-separated mixed-cluster+plaintext) | Create: `src/ServiceConnect.UnitTests/Client.RabbitMQ/ConnectionFactoryBuilderWarningTests.cs` (or matching project layout) | Regression guard |
| 3a | Investigation: confirm whether RabbitMQ.Client v7.2.1's outstanding-publisher-confirmation tracker is unbounded when `OutstandingPublisherConfirmationsRateLimiter` is unset | Read v7.2.1 source path through `BasicPublishAsync` | Decides whether 3b/3c are code or docs |
| 3b | If unbounded: set a `ConcurrencyLimiter` from `System.Threading.RateLimiting` at default permit limit 256, plumbed via `SetClientSetting("MaxOutstandingPublishConfirms", N)` | [src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs:248](../../../src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs#L248) | Memory safety under broker stall |
| 3c | If unbounded: unit test asserting the limiter is wired into `CreateChannelOptions` when `PublisherAcknowledgements == true` | New test in the producer test set | Regression guard |
| 6a | Rewrite `learn/operations/configuration.mdx#tls` for the v8 posture (TLS-on-by-default + plaintext opt-out) | `website/src/content/docs/learn/operations/configuration.mdx:44-55` | Doc accuracy |
| 6b | Spot-fix any `ConfigureTransport` snippet in `learn/getting-started.mdx` that doesn't set `SslEnabled` | `website/src/content/docs/learn/getting-started.mdx` | Doc accuracy |
| 6c | New "Breaking — TLS is on by default" subsection in `releases.mdx` v8 highlights | `website/src/content/docs/releases.mdx` | Migration narrative |

## Out of scope (deliberately)

- **`[Obsolete]` shims, transitional helpers, deprecation paths.** Clean-break v8 stance, consistent with Group A.
- **`RequireTransportSecurity` strict-mode flag** (option **b** from the question). Adds a knob without proportional value; the flip + warning is the posture.
- **Environment-aware suppression** of the warning (e.g. auto-silence in `Development`). Standard `ILogger` category filtering is the escape hatch.
- **DNS resolution for loopback detection.** TOCTOU, slow, complicates testing. A non-standard hostname like `myhost.local` resolving to `127.0.0.1` warns; opt out via log filter.
- **Automatic certificate generation or local-CA helpers.** Out of scope; the existing `CertPath` / `Certs` / `CertificateSelectionCallback` surface is sufficient for production deployments.
- **Metrics for "plaintext warnings emitted" or "rate-limiter throttle events".** Group B handles operator metrics.
- **Per-message-type concurrency split or adaptive rate-limiting** based on broker latency. Single global cap; tune via `SetClientSetting`.
- **New top-level `supported-runtimes.mdx` page.** The configuration page already covers `.NET 8/10` requirements; a dedicated page would duplicate.

---

## Item 2 — `SslEnabled = true` default

### 2a-2b. The change

```csharp
// src/ServiceConnect/Configuration/TransportConfiguration.cs:41
public bool SslEnabled { get; set; } = true;
```

When `SslEnabled = true`, [ConnectionFactoryBuilder.cs:61](../../../src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs#L61) already sets the port to `AmqpTcpEndpoint.DefaultAmqpSslPort` (5671). The flip simultaneously changes the default port from 5672 → 5671. No second change needed.

XML doc on the property — the current doc says *"Defaults to false. Consider logging a warning when disabled on non-localhost hosts."* — both clauses go. The new doc:

```csharp
/// <summary>
/// Gets or sets a value indicating whether TLS is enabled.
/// </summary>
/// <remarks>
/// <b>Defaults to <see langword="true"/> as of v8.</b> The framework connects to the broker over
/// TLS on port 5671 by default. To connect to a plaintext broker (e.g. a local RabbitMQ in
/// Docker without TLS configured), set this to <see langword="false"/>; the framework logs a
/// <see cref="LogLevel.Warning"/> when this is disabled against a non-loopback host (see
/// <c>ConfigureTransport</c> in the v8 release notes).
/// </remarks>
bool SslEnabled { get; set; }
```

The interface property `ITransportConfiguration.SslEnabled` gets a matching `<remarks>` block (the implementation's `<remarks>` is also picked up by `<inheritdoc/>` consumers, so the interface remarks are the source-of-truth).

### 2c-2d. Migration footprint

| Class | Files | Action |
|---|---|---|
| Examples | All 13 projects under `examples/` with a `UseRabbitMQ(transport => …)` block | Add `transport.SslEnabled = false; // local-dev plaintext; production must use TLS` |
| E2E tests | Each test fixture's `ConfigureTransport(t => …)` block (E2E tests use the framework-level `ConfigureTransport`, not the per-transport `UseRabbitMQ` callback) | Add `t.SslEnabled = false;` (Testcontainers RabbitMQ runs plaintext) |
| Unit tests | `TransportConfigurationTests` | Update any assertion that the default is `false` |

Every change in 2c-2d is a single-line addition to an existing `ConfigureTransport` block. No structural refactor.

### 2e. Compose file

`examples/docker-compose.yml` already maps `5672:5672` (plaintext). Stays unchanged — every example explicitly opts out of TLS to use this compose, which is correct for local-dev framing.

---

## Item 1 — Startup warning when plaintext + non-loopback

### 1a. Source-generated logger

```csharp
// src/ServiceConnect.Client.RabbitMQ/RabbitMqClientLog.cs (new)
using Microsoft.Extensions.Logging;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Source-generated logger entries emitted by the RabbitMQ client package.
/// </summary>
internal static partial class RabbitMqClientLog
{
    /// <summary>
    /// Stable event id for the plaintext-on-non-loopback warning.
    /// </summary>
    public const int PlaintextOnNonLoopbackHostEventId = 1;

    [LoggerMessage(
        EventId = PlaintextOnNonLoopbackHostEventId,
        EventName = "PlaintextOnNonLoopbackHost",
        Level = LogLevel.Warning,
        Message = "ServiceConnect transport is configured for plaintext (SslEnabled=false) against a non-loopback host '{Host}'. Production deployments should use TLS; set SslEnabled=true (the v8 default) and configure certificates. To suppress this warning in environments where plaintext is intentional, raise the ServiceConnect.Client.RabbitMQ category to Error.")]
    public static partial void PlaintextOnNonLoopbackHost(ILogger logger, string host);
}
```

Logger category resolves to `ServiceConnect.Client.RabbitMQ.RabbitMqClientLog` (the type name follows Group C's `InMemoryPersistenceLog` convention). Filterable on the `ServiceConnect.Client.RabbitMQ` prefix.

### 1b. Trigger condition

Fires from `ConnectionFactoryBuilder.Build` (the seam where producer/consumer connection setup runs once per role) when both:

1. `transport.SslEnabled == false`, **and**
2. `Host` parses to / contains a non-loopback address.

Loopback detection algorithm:
- Split `Host` on `,` (the existing comma-separated cluster-list convention).
- For each entry, trimmed:
  - `IPAddress.TryParse(entry, out var addr)` — if `true`, evaluate `IPAddress.IsLoopback(addr)`.
  - If parse fails (it's a hostname), check against the literal set `{ "localhost" }` (case-insensitive).
- The warning fires if **any** entry is non-loopback. Mixed loopback/non-loopback clusters are pathological; the conservative read is "warn if anything in there isn't loopback."

Frequency: fires once per `ConnectionFactoryBuilder.Build` call — typically once for the producer connection setup and once for the consumer, so 2 warnings on a typical bus. Acceptable: the loud signal is the point. Operators who knowingly opt for plaintext silence the category. **Not** deduplicated across producer/consumer because that would couple them through shared state for no real benefit.

### 1c. Tests

Five-case unit test using `FakeLogger<>` from `Microsoft.Extensions.Diagnostics.Testing` (already added in Group C):

1. `Host = "localhost"` + `SslEnabled = false` → no warning.
2. `Host = "localhost"` + `SslEnabled = true` → no warning.
3. `Host = "rabbit.example.com"` + `SslEnabled = false` → exactly one warning at `Warning` level with `EventId == PlaintextOnNonLoopbackHostEventId` and message containing the host name.
4. `Host = "rabbit.example.com"` + `SslEnabled = true` → no warning.
5. `Host = "localhost,rabbit-2"` + `SslEnabled = false` → one warning (because `rabbit-2` is non-loopback).

Bonus IP-parse cases worth adding inline:
- `Host = "127.0.0.1"` + `SslEnabled = false` → no warning.
- `Host = "::1"` + `SslEnabled = false` → no warning.
- `Host = "10.0.0.5"` + `SslEnabled = false` → warning fires.

Test home: alongside the existing RabbitMQ client tests in `src/ServiceConnect.UnitTests/`. Suggested filename `ConnectionFactoryBuilderWarningTests.cs`. If `ConnectionFactoryBuilder.Build` does not currently take an `ILogger`, the implementation plan threads one through; the test sets it up via DI like Group C's `InMemoryPersistenceRegistrationLogTests`.

---

## Item 3 — Publisher-confirm tracker bound

### 3a. Investigation

Open RabbitMQ.Client v7.2.1 source (NuGet package or GitHub) and trace `BasicPublishAsync` when `publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true` and `OutstandingPublisherConfirmationsRateLimiter == null`. Specifically, find the data structure that holds outstanding confirms (by delivery tag) and confirm whether it has any cap independent of the rate limiter.

Expected outcome (≥ 80% confidence based on the public API surface): the tracker is unbounded when no rate limiter is configured.

If the investigation surfaces an implicit bound (e.g. the channel itself caps at some N, or back-pressure is built into the dispatcher), 3b becomes a documentation-only commit explaining the bound and 3c is dropped.

### 3b. Bound (assuming unbounded)

Add a `ConcurrencyLimiter` from `System.Threading.RateLimiting` (already in .NET 8+ runtime, no new package) at a default of **256 outstanding publishes**. The limiter is created per channel inside `ProducerConnection.OpenAsync` and lifecycle-bound to the channel:

```csharp
// src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs:248
if (_publisherAcks)
{
    var rateLimiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
    {
        PermitLimit = _maxOutstandingConfirms,
        QueueLimit = int.MaxValue,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });
    var channelOptions = new CreateChannelOptions(
        publisherConfirmationsEnabled: true,
        publisherConfirmationTrackingEnabled: true,
        outstandingPublisherConfirmationsRateLimiter: rateLimiter);
    model = await connection.CreateChannelAsync(channelOptions, cancellationToken).ConfigureAwait(false);
}
```

The limiter goes through the existing `DisposeModelAsync` path (which already tears the channel down) — no new disposal logic.

`_maxOutstandingConfirms` reads from `ITransportConfiguration.SetClientSetting("MaxOutstandingPublishConfirms", N)` via the same pattern as the existing `PublisherAcknowledgements`, `PublishTimeout` knobs. Default `256` if unset. Hitting the limit back-pressures the publisher (the publish call awaits a permit) — it does **not** error.

`PermitLimit` rationale: 256 throttles a runaway producer well before memory matters (each tracker entry is small, but unbounded matters), high enough that no realistic non-broken publishing pattern hits the limit. Borrowed loosely from RabbitMQ's own published guidance on confirm batching ("a few hundred outstanding"); the precise number is operator-tunable.

### 3c. Test (assuming code change lands)

One unit test confirming `CreateChannelOptions.OutstandingPublisherConfirmationsRateLimiter` is non-null and is a `ConcurrencyLimiter` with `PermitLimit == 256` (or the value the user configured via `SetClientSetting`) when `PublisherAcknowledgements == true`. The actual rate-limiting behaviour is RabbitMQ.Client's responsibility; we just verify our configuration wires it up.

Mocking strategy: the existing `ProducerConnection` test scaffolding (whatever it currently uses to fake `IConnectionFactory` / `IConnection` / `IChannel`) is reused; the assertion is on the captured `CreateChannelOptions` argument passed to `IConnection.CreateChannelAsync`.

---

## Item 6 — Docs (TLS expectations)

### 6a. `configuration.mdx#tls` rewrite

Replace the existing TLS subsection at `learn/operations/configuration.mdx:44`. New body:

```markdown
### TLS

**As of v8, TLS is enabled by default.** `SslEnabled` defaults to `true`, and ServiceConnect connects on the default AMQPS port (5671). For production deployments connecting to a broker with TLS configured, no transport setup is required beyond `Host` and credentials.

For brokers behind TLS with custom server names or client certificates:

```csharp
transport.SslEnabled = true;            // (default)
transport.ServerName = "rabbit.prod.example.com";
transport.CertPath = "/etc/ssl/client.pfx";
transport.CertPassphrase = Environment.GetEnvironmentVariable("CLIENT_CERT_PASSPHRASE");
```

Client certs can also be supplied in memory via `Certs`, or selected dynamically via `CertificateSelectionCallback`. For broker chains that don't validate cleanly, `AcceptablePolicyErrors` lets you widen acceptance — use sparingly, and never `SslPolicyErrors.RemoteCertificateNameMismatch` in production.

#### Connecting to a plaintext broker (local dev)

If your broker runs without TLS — typically the official `rabbitmq:3-management` Docker image on port 5672 — set `SslEnabled = false`:

```csharp
transport.SslEnabled = false;          // local-dev plaintext; production must use TLS
```

ServiceConnect logs a `Warning`-level message under the `ServiceConnect.Client.RabbitMQ` category when plaintext is configured against a non-loopback host. To silence the warning in environments where plaintext is intentional (e.g. an isolated VPC), raise the category's minimum level to `Error` via standard `Microsoft.Extensions.Logging` filter configuration.
```

### 6b. `getting-started.mdx` spot-fix

Spot-check the page for any `ConfigureTransport` snippet that doesn't set `SslEnabled`. Implementation will pick `SslEnabled = false` (with a `// local-dev plaintext` comment) or `SslEnabled = true` (redundant but explicit) based on the snippet's framing. If the page is already TLS-clean (no plaintext-against-non-loopback snippet that would now be misleading), no change needed.

### 6c. `releases.mdx` v8 highlights subsection

Insert alongside the existing v8 entries:

```markdown
### Breaking — TLS is on by default

`TransportConfiguration.SslEnabled` defaults to `true` in v8 (was `false` in v7).
The framework now connects on AMQPS port 5671 by default; production deployments
with TLS-configured brokers no longer need any transport setup beyond `Host` and
credentials.

**Migration.** If your broker runs without TLS — most localhost dev setups do —
explicitly opt out:

```csharp
builder.ConfigureTransport(t =>
{
    t.Host = "localhost";
    t.SslEnabled = false;        // local-dev plaintext; production must use TLS
});
```

ServiceConnect emits a `Warning`-level log under the
`ServiceConnect.Client.RabbitMQ` category when plaintext is configured against a
non-loopback host. The warning is filterable via standard `Microsoft.Extensions.Logging`
category configuration; it is NOT an error and the framework still connects.

**Why.** ServiceConnect-CSharp v7 defaulted to plaintext for ergonomic localhost
dev. v8 inverts the default to align with secure-by-default norms — production
deployments connecting over plaintext to a non-loopback broker are almost
always a misconfiguration, and the warning catches the rest.
```

---

## Testing strategy

| Item | Verification |
|---|---|
| 2a-2b (default + XML docs) | Build pipeline catches malformed XML doc syntax; visual review for tone. |
| 2c-2e (examples + e2e + unit-test updates) | Full unit-test sweep + EndToEndTests build clean. Every example builds clean. |
| 1a-1c (warning) | New 5-case unit test (per Section 3) using `FakeLogger<>`. |
| 3a (investigation) | Read RabbitMQ.Client v7.2.1 source; record finding in commit message + spec annotation. |
| 3b-3c (bound + test) | New unit test asserting `OutstandingPublisherConfirmationsRateLimiter` is wired (only if 3a confirms unboundedness). |
| 6a-6c (docs) | Local Astro build (`npm run build` in `website/`) catches broken markdown / cross-link errors. |

No new automated content tests beyond the unit tests.

---

## Rollout

Five atomic commits on the existing `v7-clean-architecture` branch (consistent with how Groups A and C landed; no separate worktree).

1. **`fix(transport)!: investigate publisher-confirm tracker bound`** — Item 3 standalone first because it's investigation-driven (could land as code change OR documentation-only depending on the v7 source). Lands first so the security-flip commits don't entangle with it.
2. **`feat(transport)!: SslEnabled defaults to true`** — Item 2: property default flip + `TransportConfiguration` XML doc rewrite + every example + every E2E test fixture in one atomic commit. Reviewer sees the breaking-change blast radius in one diff.
3. **`feat(transport): warn on plaintext over non-loopback host`** — Item 1: new `RabbitMqClientLog` source-gen logger + `ConnectionFactoryBuilder` emit point + 5-case unit test. Lands after commit 2 because the warning's wording references the v8 default that landed there.
4. **`docs(website): document v8 TLS posture`** — Item 6: `configuration.mdx#tls` rewrite + `getting-started.mdx` spot-fix + `releases.mdx` v8 highlights subsection. One coherent doc commit.
5. **`docs(architecture): mark Group D done in the fix plan`** — close-out commit, same shape as Groups A and C.

Each commit independently passes build + tests; can be reviewed and reverted in isolation.

---

## Risks

- **Examples drift if a new example lands without `SslEnabled = false`.** Mitigation: the XML doc rewrite + the `releases.mdx` migration narrative make it loud. Plus the runtime warning catches the case if it actually runs against a non-loopback host. No guarding analyzer.
- **Comma-separated cluster lists with mixed loopback/non-loopback entries** are pathological. Section 1b's design treats "any non-loopback in the list" as warning-worthy, which is conservative-safe. Worst case: an operator gets a warning they consider noise. Filterable.
- **Hostname-vs-IP loopback detection** doesn't do DNS resolution. A non-standard hostname like `myhost.local` that resolves to `127.0.0.1` will still warn. Documented; opting out via the log filter is the escape hatch. The TOCTOU and slow-DNS reasons against resolving were called out in Section 1b.
- **Rate-limiter at 256** could be wrong for a specific deployment shape (very high throughput, latency tolerance). Operators tune via `SetClientSetting("MaxOutstandingPublishConfirms", N)`. If the default turns out to be too low in practice we adjust in a patch release; the code change to widen is one constant.
- **Item 3 outcome unknown until investigation lands.** If RabbitMQ.Client v7 already bounds the tracker by some other mechanism, the commit becomes documentation-only. The plan accommodates either branch.
- **Rollback.** Each of the five commits is independently revertible. `git revert` of any one commit cleanly removes that item. No data migrations, no API surface changes beyond the `SslEnabled` default.

---

## Decisions banked from the brainstorm

For audit trail:

- **TLS default flip:** option **c** (aggressive). User explicitly chose this in brainstorm with the framing "I want this to be correct and it's a major version so I'm OK with breaking changes."
- **Warning condition:** fires whenever `SslEnabled == false` AND host is non-loopback, regardless of how `SslEnabled` got to `false` (no "default vs explicit" distinction). Cleanest semantic.
- **Loopback detection:** hostname-string check + `IPAddress.IsLoopback`. **No DNS resolution.**
- **Cluster-list policy:** if any entry in a comma-separated `Host` is non-loopback, the warning fires.
- **Rate-limiter default:** 256 outstanding publishes, configurable via `SetClientSetting("MaxOutstandingPublishConfirms", N)`.
- **Item 3 commit-as-investigation:** lands first as a self-contained commit so its outcome (code or docs) doesn't entangle with the security-flip commits.
- **Rollout:** five atomic commits on `v7-clean-architecture`, mirroring Group C's structure.
