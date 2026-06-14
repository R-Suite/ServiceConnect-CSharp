# Stress Harness

## Overview

Single-process harness that drives every ServiceConnect pattern across two `Bus` instances concurrently. Catches cross-tenant routing leaks between buses, shared static / singleton state escaping between tenants, deadlocks under sustained dispatch, lifecycle races around `Bus.DisposeAsync`, and memory leaks under continuous load.

Unlike the per-pattern example projects under `examples/<Pattern>`, the harness is not a worked tutorial. It is an end-to-end driver that exercises the public surface in adversarial conditions and asserts framework invariants. Use it as the smoke test before tagging a release, or as the soak before promoting a behavioural change to the routing or filter pipelines.

## Modes

| Mode | What it does | Default duration |
|---|---|---|
| `smoke` | Run every pattern once in both directions (alpha to beta and beta to alpha). Fail-loud, CI-friendly. | ~30s |
| `soak` | Loop every pattern continuously for `--duration`; track GC memory baseline vs final and fail if growth exceeds `--memory-budget-mb`. | 5 min |
| `throughput` | Rate-controlled at `--rate` flows/sec/pattern; report p50/p95/p99 latency per pattern. | 5 min |

## Prerequisites

`docker compose -f docker-compose.yml up -d` from this directory. The compose file brings up RabbitMQ (5672 + 15672 management UI) and MongoDB (27017) with health checks.

## Run This Example

`bash run.sh`

The runner brings the docker stack up, waits for both services to be healthy, runs the harness, and tears the stack down on exit. Configuration is environment-variable driven so the same script covers every mode:

```bash
./run.sh                                                  # smoke, in-memory persistence
MODE=soak DURATION=00:02:00 ./run.sh                      # 2-minute soak
MODE=throughput RATE=50 DURATION=00:00:30 ./run.sh        # rate-controlled throughput
PERSISTENCE=mongo ./run.sh                                # MongoDB-backed saga + aggregator
```

On Windows use `.\run.ps1`; the same environment-variable contract applies.

## Run Manually

`run.sh` is a thin shell over `dotnet run`. The full CLI surface:

```text
dotnet run --project src/ServiceConnect.Examples.StressHarness -- \
  [--mode smoke|soak|throughput]      # default: smoke
  [--duration HH:MM:SS]               # soak / throughput run length
  [--rate <int>]                      # throughput: flows/sec/pattern
  [--patterns p2p,pubsub,...]         # default: every pattern
  [--persistence inmemory|mongo]      # default: inmemory
  [--chaos none|docker]               # default: none (docker requires --mode soak)
  [--chaos-interval HH:MM:SS]         # time between kill events (default 30s)
  [--chaos-downtime HH:MM:SS]         # time broker stays down (default 20s)
  [--chaos-recovery-budget HH:MM:SS]  # wait after duration before recovery check (default 60s)
  [--broker amqp://localhost]
  [--flow-timeout HH:MM:SS]
  [--memory-budget-mb <int>]          # soak budget (default 256 MB)
  [--report-dir out/]
```

The default budget is calibrated for the standard 5-minute soak across all 14 patterns. A 5-minute run processes roughly 100 k flows; per-pattern result history, framework state, and RabbitMQ.Client buffers contribute around 133 MB of expected steady-state heap (~1.3 KB per flow). 256 MB gives that baseline comfortable headroom while still catching gross regressions. Longer soaks or higher-rate throughput runs will accumulate more in-flight state; raise the budget via `--memory-budget-mb` if the soak's flow assertions are green but the process-level memory check trips.

## Output

- `out/report.json` — structured per-pattern stats, per-bus counters, memory baseline/final, assertion outcomes, latency histograms in throughput mode, and (when `--chaos docker` was set) the chaos kill timeline and per-pattern window breakdown. Schema version pinned at `reportVersion: 3`.
- `out/report.md` — human-readable summary suitable for paste-into-a-PR.
- Exit code:
  - `0` — every flow passed and every process-level assertion held.
  - `1` — at least one flow failed or a process-level assertion fired.
  - `2` — CLI or startup error (bad argument, broker unreachable, etc.).

## What this harness catches

- **Cross-tenant routing leaks** — handler dispatched on the wrong `Bus` instance.
- **Shared static / singleton state** — leakage across buses surfacing as wrong tenant on the receiving handler.
- **Deadlocks under sustained dispatch** — soak mode runs concurrent flows for the configured duration; a deadlock manifests as flow-timeout failures.
- **Lifecycle races** — `Bus.DisposeAsync` is invoked while flows are mid-dispatch; the lifecycle assertion confirms in-flight work completes or fails cleanly.
- **Memory leaks** — soak mode samples `GC.GetTotalMemory` at the start and end and fails if growth exceeds `--memory-budget-mb`.
- **Idempotency races** — a handler firing more than the expected count for a given flow id surfaces in the per-handler accounting.

## Chaos mode

Pass `--chaos docker` to a soak run to have the harness periodically stop and start the broker container while the soak runs:

```bash
MODE=soak DURATION=00:05:00 CHAOS=docker ./run.sh
```

This exercises the framework's auto-recovery code paths (connection auto-recovery, channel restart, consumer-tag-change handling) — code that smoke and non-chaos soak runs can't reach.

| Flag | Default | What it controls |
|---|---|---|
| `--chaos docker` | (off — defaults to `none`) | Enable chaos. Only supported with `--mode soak`. |
| `--chaos-interval` | `30s` | Time between kill events. |
| `--chaos-downtime` | `20s` | How long the broker stays down before restart. |
| `--chaos-recovery-budget` | `60s` | Wait after the soak's `--duration` ends before the recovery assertion fires. |

### Durability contract under chaos

The harness wires the transport for at-least-once delivery across a broker
restart by explicitly setting two flags on its `UseRabbitMQ` configuration:

- `Durable = true` — queue declarations survive broker restart (the broker
  reloads queue + binding metadata from disk on boot).
- `PublisherAcknowledgements = true` — `Bus.SendAsync` / `PublishAsync`
  awaits the broker's confirm before completing, so a publish in flight
  when the broker is killed surfaces as an exception to the caller rather
  than a silent drop.

Both values match the framework defaults; declaring them in the harness is
belt-and-braces against a future default change rotating chaos runs back
into silent-loss territory. Delivery-mode 2 (broker fsyncs each message
before ack'ing) is set unconditionally by the framework's
`OutboundHeaderBuilder` and needs no opt-in.

### Assertion model

**Hard (exit 1 if failed):** after the recovery budget elapses, both `Bus α` and `Bus β` report `IsConsuming == true`. If either remains unhealthy, the run fails.

**Soft (reported, never fails the run):** per-pattern flow counts broken down by chaos window (`pre-chaos`, `during-chaos`, `in-recovery`, `post-chaos`), kill-event timeline in `out/report.md`. The "under-handled flow count" (flows the driver sent that hadn't completed by end-of-soak) measures in-flight loss at broker death — messages whose `SendAsync` returned before the broker accepted them, or whose handler was mid-dispatch when the broker died. A non-zero count is a measurement, not a recovery failure; the hard `IsConsuming` assertion is the recovery-success signal.

### Scope

This mode exercises single-broker restart only. Multi-node cluster failover (where the AMQP client transparently re-targets a surviving node) is a deferred follow-up — `docker-compose.cluster.yml` ships in the repo for that work but isn't used by this mode.

## Pattern coverage

| Pattern | Driver |
|---|---|
| PointToPoint | `PointToPointDriver` |
| PublishSubscribe | `PublishSubscribeDriver` |
| RequestReply | `RequestReplyDriver` |
| CompetingConsumers | `CompetingConsumersDriver` |
| ContentBasedRouting | `ContentBasedRoutingDriver` |
| PolymorphicMessages | `PolymorphicMessagesDriver` |
| Filters | `FiltersDriver` |
| ProcessManager | `ProcessManagerDriver` (requires persistence) |
| Aggregator | `AggregatorDriver` (requires persistence) |
| ScatterGather | `ScatterGatherDriver` |
| RoutingSlip | `RoutingSlipDriver` |
| Streaming | `StreamingDriver` |
| CustomFilterAndMiddleware | `CustomFilterAndMiddlewareDriver` |
| Telemetry | `TelemetryDriver` |

Fourteen patterns x two directions = 28 flows per smoke run.
