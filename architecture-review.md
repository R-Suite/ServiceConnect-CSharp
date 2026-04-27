# Architecture review — improvement phases

Findings from a structural review of `src/` (2026-04-27). Each phase is independent and can be planned, scheduled, and shipped on its own. Phases are ordered roughly by risk — earliest are pure hygiene with no behavioural change; later phases involve API decisions or cross-cutting moves.

## Verdict on the current split

The project boundaries are right. Dependency arrows go the correct way (`Interfaces` → `ServiceConnect` → adapters → tests) with no cycles or back-edges. `Interfaces` has zero NuGet deps. Persistence adapters don't reference each other. `Filters.MessageDeduplication` depends only on `Interfaces`. `InternalsVisibleTo` is narrowly scoped. Most of the wins below are inside two or three projects rather than across the boundary lines.

---

## Phase 1 — Repo hygiene

Pure cleanup. No behavioural change, no API change, no cross-project moves. Each item is independent.

**1a. Delete `src/.nuget/`**
Holds `NuGet.exe`, `NuGet.targets`, and an old `NuGet.Config` from the pre-SDK-style era. Nothing in the modern build references it. Listed in `ServiceConnect.sln` as a solution-items folder; remove that entry too.

**1b. Prune dead `.sln` configuration matrix or migrate to `.slnx`**
[ServiceConnect.sln](src/ServiceConnect.slnx) carries `Mixed Platforms`, `x64`, `x86` configurations across every project — about 80% of the file is dead config for a managed-only library. Two options:
- Prune in place: keep only `Debug|Any CPU` and `Release|Any CPU`.
- Migrate to the new XML `.slnx` format (Visual Studio 17.10+ / `dotnet sln migrate`).

**1c. Add a "Persistence" solution folder**
`ServiceConnect.sln` already groups `Tests` and `Clients`. Add a `Persistence` folder for `Persistence.InMemory` and `Persistence.MongoDb` for symmetry.

**1d. Remove `ServiceConnect.Interfaces/Properties/AssemblyInfo.cs` if vestigial**
SDK-style projects don't usually need it. Verify nothing inside it is still load-bearing (e.g. an `[assembly: …]` attribute that isn't expressed in the `.csproj`), then delete the folder.

**Verification.** `dotnet build` of the solution succeeds. `dotnet test` runs the same set of tests as before.

**Rough effort.** Half a day total. Each item is a standalone PR.

---

## Phase 2 — Resolve `Filters.MessageDeduplication` status

The filter project lives under `filters/` outside `src/` and is **not** in `ServiceConnect.slnx`. `dotnet build` from the solution silently misses it; CI on the solution will too. It also uses `Common.Logging` while everything else uses `Microsoft.Extensions.Logging.Abstractions`. Decide what this project is, then make the structure match the decision.

**2a. Decide intent**
- *Option A — first-party plugin shipped with the library:* move under `src/` and add to `ServiceConnect.slnx`. CI builds it alongside the rest.
- *Option B — separate package living next to the library:* keep under `filters/`, give it its own solution, document in the README that it ships independently.

**2b. Normalize logging dependency**
Whichever option above is chosen, replace `Common.Logging` with `Microsoft.Extensions.Logging.Abstractions` so the filter project lines up with everything else. `Common.Logging` is essentially abandoned.

**Verification.** Build of whichever solution(s) include the project succeeds; test project still passes; package metadata reflects the new logging dep.

**Rough effort.** Half a day for the move + sln update; another half-day for the logging migration.

---

## Phase 3 — Folder restructure inside flat projects

Pure file moves; no `using` changes if namespaces stay flat per project (which they currently do). Three projects, can be done in any order, one PR each.

**3a. `ServiceConnect.Client.RabbitMQ/`** — currently 16 `.cs` files at the project root.
Suggested grouping:
- `Connection/` — `Connection`, `ConnectionFactoryBuilder`, `IServiceConnectConnection`, `SslConfigurationBuilder`
- `Producer/` — `Producer`
- `Consumer/` — `Consumer`, `RabbitMqConsumerHost`, `MessageRetryHandler`, `Retry`
- `Topology/` — `RabbitMqTopologyProvisioner`, `RabbitMqQueueNaming`
- `Audit/` — `MessageAuditPublisher`
- `Configuration/` — `RabbitMQSettingKeys`, `RabbitMQExtensions`, `HeaderHelpers`

**3b. `ServiceConnect.Persistence.InMemory/`** — 19 files flat.
Suggested grouping (mirror Mongo if 3c is done in the same window):
- `Aggregator/` — `InMemoryAggregatorPersistor`, related entries
- `ProcessManager/` — `InMemoryProcessManagerFinder`, `ProcessManagerPredicateCache`
- `Timeout/` — `InMemoryTimeoutStore`, `TimeoutEntry`, `TimeoutEntryComparer`
- `Cache/` — `CacheProvider`, `CacheItem`, `CacheItemPriority`, `SlidingDetails`, `KeyRemovedEventArgs`, `IKeyValueStore`, `ICacheProvider`, `MemoryData`, `DeepClone`
- root: `InMemoryPersistenceState`, `InMemoryPersistenceExtensions`

**3c. `ServiceConnect.Persistence.MongoDb/`** — 20 files flat. Mirror the InMemory grouping (`Aggregator/`, `ProcessManager/`, `Timeout/`, `Configuration/`).

**Verification.** Build passes; no `.cs` file moves between projects, only inside; all existing tests still pass.

**Rough effort.** One PR per project. Each is roughly one to two hours including test runs.

---

## Phase 4 — Split the large files

Two files in `Client.RabbitMQ/` are doing too many things at once. This is a proper refactor: behaviour-preserving but it touches real code, so it needs more care than Phase 3.

**4a. Split [Producer.cs](src/ServiceConnect.Client.RabbitMQ/Producer.cs) (31 KB)**
Likely seams to extract:
- Channel-pool lifecycle and acquisition
- Publisher confirms tracking and timeouts
- Outbound retry policy / Retry interaction
- Header / property mapping for outgoing envelopes
The public surface — whatever `IProducer` (or equivalent) callers depend on — should not move. Extracted classes can stay `internal`.

**4b. Split [RabbitMqConsumerHost.cs](src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs) (33 KB)**
Likely seams:
- Consumer/channel lifecycle (open, recover, dispose)
- Message receive loop and dispatch into the bus pipeline
- Ack/nack and requeue policy
- Error-queue routing on poison messages

**4c. (optional) [Bus.cs](src/ServiceConnect/Bus.cs) (29 KB) and [ServiceCollectionExtensions.cs](src/ServiceConnect/ServiceCollectionExtensions.cs) (21 KB)**
Less urgent. `ServiceCollectionExtensions` is the better candidate — splitting registration helpers per concern (transport, persistence, telemetry, handlers, filters) reads better than one giant builder file.

**Verification.** Full unit + E2E test runs unchanged; the new concurrency suite added in this branch is a useful extra signal here.

**Rough effort.** Each file split is a focused 1–2 day effort with tests. Don't pile them into one PR.

---

## Phase 5 — `InternalsVisibleTo` audit

[ServiceConnect.csproj](src/ServiceConnect/ServiceConnect.csproj) exposes internals to two assemblies: `ServiceConnect.UnitTests` (fine, tests are throwaway) and `ServiceConnect.Client.RabbitMQ` (versioning concern). `Client.RabbitMQ` ships as a separate NuGet package but is coupled to `ServiceConnect`'s internal API surface. Bumping core internals forces the RabbitMQ client to move in lockstep, and a consumer who pins the two packages to mismatched versions will get a runtime surprise.

**5a. Inventory the actual usage**
Grep `Client.RabbitMQ/` for every reference to a type or member in `ServiceConnect/` that would be inaccessible without `InternalsVisibleTo`. Categorize each:
- *Genuinely private to core, accidental leak from the client.* Refactor the client to not need it.
- *Stable enough to be public API.* Promote it, document it.
- *Useful only to first-party adapters, will never be supported externally.* Accept lockstep versioning, document it explicitly in the README so consumers understand the constraint.

**5b. Pick one of the categories per item and execute.**
Outcome should be a small, well-defined public extension surface for adapter authors, plus an explicit "internal API, lockstep with core version" callout for whatever stays internal.

**Verification.** `Client.RabbitMQ` builds without `InternalsVisibleTo` (if everything is now public), or the README clearly documents the coupling (if not). Existing tests pass.

**Rough effort.** Inventory: half a day. Execution depends on how much leaks — could be anywhere from a day to a week.

---

## Phase 6 — Multi-targeting policy

[Directory.Build.props](src/Directory.Build.props) pins `LangVersion` per TFM, but the actual TFM list (`net8.0;net10.0`) is repeated in every `.csproj`. The choice deliberately uses recent BCL features (`System.Threading.Lock`, etc.) and skips `netstandard2.x`, which cuts off netfx and LTS-on-netcoreapp consumers. That's a fine choice — but it should be a deliberate, documented one, not drift.

**6a. Decide and document**
- *Option A — modern .NET only, by design.* Add a "Supported runtimes" section to the README that says `net8.0+`. Keep current TFM list.
- *Option B — broaden reach.* Add `netstandard2.1` (and likely `net6.0` for in-LTS-window users). Requires guarding `System.Threading.Lock` and similar with `#if NET9_0_OR_GREATER` blocks (already done in spots).

**6b. Centralize TFMs**
Move `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>` into `Directory.Build.props` so adding/removing a TFM is one edit instead of seven.

**Verification.** `dotnet build` of every project passes for every TFM; package consumers in the chosen runtime list can install and use the library.

**Rough effort.** Decision + README: an hour. Centralizing TFMs: an hour. Adding `netstandard2.1` (Option B): a couple of days, depending on what's currently behind `#if NET9_0_OR_GREATER`.

---

## Cross-phase notes

- **Phases 1, 2, 3 are independent of everything else.** Schedule freely.
- **Phase 4 should follow Phase 3** — splitting a giant file is much easier when its current responsibilities have a folder home for the pieces.
- **Phase 5 is independent of 3 and 4** but the inventory will be cleaner after Phase 4 because seams will already be visible.
- **Phase 6 touches every `.csproj`** — coordinate so it doesn't conflict with PRs in other phases.
