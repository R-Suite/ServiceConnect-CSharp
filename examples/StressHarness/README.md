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
  [--chaos none]                      # only 'none' accepted today
  [--broker amqp://localhost]
  [--flow-timeout HH:MM:SS]
  [--memory-budget-mb <int>]          # soak budget (default 50 MB)
  [--report-dir out/]
```

## Output

- `out/report.json` — structured per-pattern stats, per-bus counters, memory baseline/final, assertion outcomes, and latency histograms in throughput mode. Schema version pinned at `reportVersion: 1`.
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

## What this harness does NOT catch

Broker-side fault behaviour — node kills, failover correctness, connection auto-recovery, channel restart, message loss under broker chaos — is out of scope for this harness. The `IBrokerChaos` contract is shipped inert (`NoopBrokerChaos`) so a future chaos / failover companion harness can plug in without a contract change. The `docker-compose.cluster.yml` file in this directory is the 3-node RabbitMQ cluster that companion will run against; it is not used by `run.sh` today.

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
