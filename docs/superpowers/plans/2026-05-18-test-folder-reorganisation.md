# UnitTests Folder Reorganisation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the ~106 test files currently sitting at the root of `src/ServiceConnect.UnitTests/` into topic-grouped subfolders. The project's existing 19 subfolders are unevenly populated (71 in RabbitMQ, 22 in Processors, 22 in Persistence, ≤3 in many others); this plan finishes the partial reorganisation that has already started.

**Architecture:** One upfront analysis commit (Task 0) to settle the namespace strategy and rename the `Bus/` folder to avoid shadowing. Then one commit per destination folder (Tasks 1-9), each grouping ~10-25 files with a clean `git mv` + namespace update + build/test cycle. Each commit is self-contained and individually reviewable. The pattern: pick the target folder → `git mv` the files in → bulk-update each file's `namespace` line → verify build + tests → commit. Three files stay at the root (xUnit collection fixtures + `ModuleInit`) because they have cross-folder consumers.

**Tech Stack:** Same as before. `dotnet build`/`dotnet test` delegated to subagents per the user's auto-memory. Build target is just `src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1` for every task in this plan — production projects don't change.

---

## Background: why the `Bus/` folder needs renaming

The existing `Bus/` folder has files using **two different namespaces** (`ServiceConnect.UnitTests.BusInterface` and `ServiceConnect.UnitTests.Events`), neither matching the folder name. The reason: a hypothetical namespace `ServiceConnect.UnitTests.Bus` would shadow the production type `ServiceConnect.Bus` for any test inside the folder that references the `Bus` class directly (CS0118 "Bus is a namespace but used like a type" — see Task 6 of the pre-release-fixes branch where this was discovered).

I checked the root `ServiceConnect` namespace for other type-vs-folder-name shadowing risks. The exhaustive list of root-level types is:
- `Bus` (internal sealed class) ← only real conflict
- `ServiceConnectBuilder` (public sealed class) — too long to ever be a folder name
- `ServiceConnectLog` (internal partial logger class) — also no risk

**Resolution:** rename the test folder from `Bus/` to `BusTests/` and use namespace `ServiceConnect.UnitTests.BusTests`. This is unambiguous and matches the "test class for the Bus type" intent.

---

## File mapping

### Stays at the root (3 files)

| File | Reason |
|---|---|
| `ModuleInit.cs` | xUnit module initialiser; convention is project-root. |
| `MongoBsonSerialCollection.cs` | xUnit collection-fixture class referenced via `[Collection("...")]` attribute. Moving it risks breaking `using`-less attribute references in dependent tests. |
| `SerialConcurrencyCollection.cs` | Same as above. |

### Moves (and target folders)

| Target folder (and namespace suffix) | Approx count | Files (root → subfolder) |
|---|---|---|
| `BusTests/` (renamed from `Bus/`) — `ServiceConnect.UnitTests.BusTests` | ~18 | All 15 `Bus*Tests.cs` from the root + the 3 existing files in `Bus/` (with namespace harmonised). Includes `BusTests.cs`, `BusConfigurationTests.cs`, `BusCreateStreamValidationTests.cs`, `BusDisposeBoundedWaitTests.cs`, `BusEnvelopeMessageTypeTests.cs`, `BusIsConsumingDuringDisposeTests.cs`, `BusIsConsumingTests.cs`, `BusLifecycleCancellationTests.cs`, `BusOutboundPreparationTests.cs`, `BusRouteValidationTests.cs`, `BusSendToManyAsyncFanoutTests.cs`, `BusStartConsumingFlagOrderTests.cs`, `BusStopConsumingIdempotenceTests.cs`, `BusStopConsumingTransportHookTests.cs`, `BusTransportLifecycleTests.cs`, plus `BusHostedServiceMissingProducerTests.cs` (already in `Bus/`), `ConsumeEventArgsReadOnlyHeadersTests.cs` (already in `Bus/`, but namespace `Events` — leave that one in `Events/` instead), `RequestTimeoutAsyncDimTests.cs` (already in `Bus/`). |
| `Services/` (existing, namespace `ServiceConnect.UnitTests.Services`) | +20 | `RequestReplyManager*Tests.cs` (8 files), `MessageDispatcher*Tests.cs` (3), `MessageBus*StreamTests.cs` (2), `FilterPipelineTests.cs`, `SendMessagePipelineTests.cs`, `ConsumeContext*Tests.cs` (3), `HandlerScanner*Tests.cs` (2), `MessageTypeExchangeNameTests.cs`, `MessageTypeRegistryTests.cs`, `RegistryInitializerTests.cs`, `SystemTextJsonMessageSerializerTests.cs`. |
| `RabbitMQ/` (existing, namespace `ServiceConnect.UnitTests.RabbitMQ`) | +12 | `ConnectionTests.cs`, `ConnectionFactoryBuilderTests.cs`, `ConsumerTests.cs`, `Producer*Tests.cs` (5), `RabbitMq*Tests.cs` (3), `RabbitMQExtensionsTests.cs`, `HeaderHelpersTests.cs`, `MessageAuditPublisherTests.cs`, `MessageRetryHandlerTests.cs`, `RetryTests.cs`, `SslConfigurationBuilderTests.cs`. |
| `Persistence/InMemory/` (new, namespace `ServiceConnect.UnitTests.Persistence.InMemory`) | ~10 | All `InMemory*Tests.cs` (9), `CacheProvider*Tests.cs` (2). |
| `Persistence/MongoDb/` (existing if Task 4 of pre-release-fixes already created it, namespace `ServiceConnect.UnitTests.Persistence.MongoDb`) | +24 | All `Mongo*Tests.cs` from the root + `MongoClientFactory*Tests.cs` (3). The file from Task 4 of pre-release-fixes (`MongoDbPersistenceOptionsAggregatorLeaseTests.cs`) already lives in this folder. |
| `Configuration/` (new, namespace `ServiceConnect.UnitTests.Configuration`) | ~8 | `BusConfigurationTests.cs` (only if it tests `IBusConfiguration` shape vs. wiring — decide; if it's wiring it belongs in `BusTests/`), `QueueConfiguration*Tests.cs` (2), `TransportConfigurationTests.cs`, `RequestOptionsTests.cs`, `SubConfigurationFreezeTests.cs`, `PersistenceConfigurationDefaultTests.cs`, `ConfigurationCleanupTests.cs`. |
| `Builder/` (new, namespace `ServiceConnect.UnitTests.Builder`) | ~3 | `ServiceConnectBuilder*Tests.cs` (2), `ServiceCollectionExtensionsTests.cs`, `PersistenceRegistrationTests.cs`. |
| `Headers/` (existing, namespace `ServiceConnect.UnitTests.Headers`) | +1 | `HeaderDecoderTests.cs`. |
| `Exceptions/` (existing, namespace `ServiceConnect.UnitTests.Exceptions`) | +2 | `ExceptionShapeTests.cs`, `InterfaceCleanupTests.cs`. |
| `Handlers/` (existing) | +2 | `HandlerScannerTests.cs`, `HandlerScannerExceptionBreadthTests.cs`, `HandlerContextNullabilityTests.cs`. (Or: leave in `Services/` if they're really about dispatch wiring rather than the scan/registration surface. Decide.) |

### Also cleanup

- `TestResults/` directory is empty and shouldn't be in source control. Add to `.gitignore` if not already (check it's not already covered by `**/TestResults/**`).
- Verify no `.trx` or other test artefact files are committed.

---

## Pre-flight (every task)

1. `which dotnet` returns `/home/tim/.local/bin/dotnet`.
2. `git status` clean (or only the task's files).
3. Branch: `v7-clean-architecture` (or wherever the in-flight branch is).

---

## Task 0: Strategy commit — rename `Bus/` to `BusTests/`

**Files:**
- Rename folder: `src/ServiceConnect.UnitTests/Bus/` → `src/ServiceConnect.UnitTests/BusTests/`
- Modify (post-rename): the 3 files inside, harmonising namespaces.

**Why:** Settle the only namespace-shadowing risk before any moves, so subsequent tasks don't have to revisit. After this task, `BusTests/` is ready to receive the 15 `Bus*Tests.cs` files from the root.

- [ ] **Step 1: Rename the folder**

```bash
cd /home/tim/source/ServiceConnect-CSharp
git mv src/ServiceConnect.UnitTests/Bus src/ServiceConnect.UnitTests/BusTests
```

- [ ] **Step 2: Harmonise namespaces inside `BusTests/`**

For each file in `BusTests/`, update the namespace line to `ServiceConnect.UnitTests.BusTests;`. The existing files are:
- `BusHostedServiceMissingProducerTests.cs` (namespace `ServiceConnect.UnitTests.BusInterface;`) → change to `ServiceConnect.UnitTests.BusTests;`
- `RequestTimeoutAsyncDimTests.cs` (namespace `ServiceConnect.UnitTests.BusInterface;`) → change to `ServiceConnect.UnitTests.BusTests;`
- `ConsumeEventArgsReadOnlyHeadersTests.cs` (namespace `ServiceConnect.UnitTests.Events;`) → **this file does not belong in BusTests/**. Move it to `Events/` (which already exists):
  ```bash
  git mv src/ServiceConnect.UnitTests/BusTests/ConsumeEventArgsReadOnlyHeadersTests.cs src/ServiceConnect.UnitTests/Events/
  ```
  Namespace stays as `ServiceConnect.UnitTests.Events;` — no edit needed.

For the two namespace edits, the precise Edit per file:
```
old_string: namespace ServiceConnect.UnitTests.BusInterface;
new_string: namespace ServiceConnect.UnitTests.BusTests;
```

- [ ] **Step 3: Find references to the old `BusInterface` namespace**

```bash
grep -rn "ServiceConnect.UnitTests.BusInterface" src/ --include='*.cs' 2>/dev/null | grep -v '/bin/\|/obj/'
```

Expected: only references inside files we're editing in this commit, no other references. If any other file imports `ServiceConnect.UnitTests.BusInterface`, update it.

- [ ] **Step 4: Build + test**

Delegate: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1`. Expect all pass.

- [ ] **Step 5: Commit**

```
chore(tests): rename Bus/ folder to BusTests/ and harmonise namespace

The Bus/ folder used two different namespaces (BusInterface, Events) to dodge
type-vs-namespace shadowing of ServiceConnect.Bus. Renaming the folder to
BusTests/ with namespace ServiceConnect.UnitTests.BusTests resolves the
shadowing while letting the namespace match the folder. The misplaced
ConsumeEventArgsReadOnlyHeadersTests.cs (namespace Events) moves to the
existing Events/ folder where it belongs.
```

---

## Task 1: Move all root-level `Bus*Tests.cs` into `BusTests/`

**Files:** ~15 file moves from `src/ServiceConnect.UnitTests/Bus*Tests.cs` (root) into `src/ServiceConnect.UnitTests/BusTests/`.

**Why:** Largest single root-level cluster. Tackle first because the namespace target is now clean (Task 0 settled it).

- [ ] **Step 1: Enumerate the files**

```bash
cd /home/tim/source/ServiceConnect-CSharp
find src/ServiceConnect.UnitTests -maxdepth 1 -name 'Bus*Tests.cs' -type f
```

Expect ~15 entries.

- [ ] **Step 2: Move with git mv**

```bash
cd /home/tim/source/ServiceConnect-CSharp
for f in $(find src/ServiceConnect.UnitTests -maxdepth 1 -name 'Bus*Tests.cs' -type f); do
  git mv "$f" src/ServiceConnect.UnitTests/BusTests/
done
```

- [ ] **Step 3: Update namespaces**

For each moved file, change `namespace ServiceConnect.UnitTests;` to `namespace ServiceConnect.UnitTests.BusTests;`. Use the Edit tool (do NOT use `sed -i`):

For each file, apply:
```
old_string: namespace ServiceConnect.UnitTests;
new_string: namespace ServiceConnect.UnitTests.BusTests;
```

- [ ] **Step 4: Resolve any broken `using`s**

After the move, the test files may reference internal types from `ServiceConnect` that were previously implicitly available. Most should still work because the using directives at the top of each file haven't changed. Compile to find out.

- [ ] **Step 5: Build and test**

`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1`. Expect: all pass. If there are compile errors, they're most likely:
  - **CS0118 (namespace-shadowing):** a test references a type `X` where `ServiceConnect.UnitTests.BusTests.X` is now resolvable (none should be — `BusTests` doesn't contain a type that shadows anything). Investigate.
  - **CS0234 ("namespace 'BusTests' does not exist..."):** a test in a different folder was using `ServiceConnect.UnitTests.Bus` as a namespace — update the using to `ServiceConnect.UnitTests.BusTests`.

- [ ] **Step 6: Commit**

```
chore(tests): move root-level Bus*Tests.cs files into BusTests/ folder

15 Bus*Tests files moved with git mv; namespace updated to
ServiceConnect.UnitTests.BusTests. No behaviour changes, no assertion edits.
```

---

## Task 2: Move `Services/`-bound tests

**Files:** ~20 files from root into `src/ServiceConnect.UnitTests/Services/`.

Files to move (read each carefully and confirm it's a "services" test, not a "bus surface" test):
- `RequestReplyManager*Tests.cs` (8 files: `RequestReplyManagerTests`, `RequestReplyManagerCallbackReentrancyTests`, `RequestReplyManagerConcurrencyTests`, `RequestReplyManagerFaultSuppressionTests`, `RequestReplyManagerInFlightCounterTests`, `RequestReplyManagerSendCancelTests`, `RequestReplyManagerSendRequestMultiUnderDeliveryTests`, `RequestReplyManagerTryHandleReplyCancelRaceTests`, `RequestReplyManagerUnobservedFaultObserverTests`).
- `MessageDispatcher*Tests.cs` (3 files: `MessageDispatcherTests`, `MessageDispatcherReplyWithoutManagerTests`, `MessageDispatcherUnresolvedTypeTests`).
- `MessageBusReadStreamTests.cs`, `MessageBusWriteStreamTests.cs`.
- `FilterPipelineTests.cs`, `SendMessagePipelineTests.cs`.
- `ConsumeContextTests.cs`, `ConsumeContextPoolConcurrencyTests.cs`, `ConsumeContextStrictReplyValidationTests.cs`.
- `MessageTypeExchangeNameTests.cs`, `MessageTypeRegistryTests.cs`, `RegistryInitializerTests.cs`.
- `SystemTextJsonMessageSerializerTests.cs`.

- [ ] **Step 1: Move with git mv** (similar pattern to Task 1, batch by file-glob).

```bash
cd /home/tim/source/ServiceConnect-CSharp
for pattern in "RequestReplyManager" "MessageDispatcher" "MessageBus" "FilterPipeline" "SendMessagePipeline" "ConsumeContext" "MessageTypeExchangeName" "MessageTypeRegistry" "RegistryInitializer" "SystemTextJsonMessageSerializer"; do
  for f in $(find src/ServiceConnect.UnitTests -maxdepth 1 -name "${pattern}*Tests.cs" -type f); do
    git mv "$f" src/ServiceConnect.UnitTests/Services/
  done
done
```

- [ ] **Step 2: Update namespaces** to `ServiceConnect.UnitTests.Services;` for each moved file (Edit tool, same pattern as Task 1).

- [ ] **Step 3: Build + test** — expect 0 failed.

- [ ] **Step 4: Commit**

```
chore(tests): move service-layer tests into Services/ folder

20 tests covering RequestReplyManager, MessageDispatcher, stream surfaces,
filter / send pipelines, ConsumeContext pool, MessageTypeRegistry, the JSON
serializer, and RegistryInitializer moved to Services/.
```

---

## Task 3: Move `RabbitMQ/`-bound tests

**Files:** ~12 files from root into `src/ServiceConnect.UnitTests/RabbitMQ/`.

Files: `Connection*Tests.cs` (2), `Consumer*Tests.cs` (1), `Producer*Tests.cs` (5: `ProducerHeaderAuthorityTests`, `ProducerInternals`, `ProducerLifecycleTests`, `ProducerRetryJitterTests`, `ProducerRetryTests`, `ProducerSizeLimitTests`), `RabbitMq*Tests.cs` (3: `RabbitMqConsumerHostTests`, `RabbitMqTopologyProvisionerTests`, `RabbitMQExtensionsTests`), `HeaderHelpersTests`, `MessageAuditPublisherTests`, `MessageRetryHandlerTests`, `RetryTests`, `SslConfigurationBuilderTests`.

**Note:** the existing `RabbitMQ/` folder already has 71 files with namespace `ServiceConnect.UnitTests.RabbitMQ`. Be careful not to introduce a duplicate filename collision.

- [ ] **Step 1: Check for filename collisions**

```bash
cd /home/tim/source/ServiceConnect-CSharp
for f in $(find src/ServiceConnect.UnitTests -maxdepth 1 -type f -name '*.cs'); do
  base=$(basename "$f")
  if [ -f "src/ServiceConnect.UnitTests/RabbitMQ/$base" ]; then
    echo "COLLISION: $base already exists in RabbitMQ/"
  fi
done
```

If collisions exist, decide per file (probably one is a rename of the other; merge or rename one).

- [ ] **Step 2: Move**

(Batch via the same `git mv` pattern as Task 2, filtering on the listed filenames.)

- [ ] **Step 3: Update namespaces** to `ServiceConnect.UnitTests.RabbitMQ;`.

- [ ] **Step 4: Build + test.**

- [ ] **Step 5: Commit**

```
chore(tests): consolidate RabbitMQ tests in the RabbitMQ/ folder

Moves the 12 root-level RabbitMQ-domain tests (Connection, Consumer, Producer,
RabbitMQExtensions, header/retry/SSL surfaces) into the existing RabbitMQ/
folder so all 83 transport tests live alongside each other.
```

---

## Task 4: Create `Persistence/InMemory/` and move InMemory tests

**Files:** ~11 root-level files → `src/ServiceConnect.UnitTests/Persistence/InMemory/`.

Files: `InMemory*Tests.cs` (9: `InMemoryAggregatorPersistorConcurrencyTests`, `InMemoryAggregatorPersistorCountResolvedTests`, `InMemoryAggregatorPersistorTests`, `InMemoryProcessManagerFinderCloneCountTests`, `InMemoryProcessManagerFinderConcurrencyTests`, `InMemoryProcessManagerFinderTests`, `InMemoryTimeoutStoreConcurrencyTests`, `InMemoryTimeoutStoreTests`), `CacheProviderTests.cs`, `CacheProviderConcurrencyTests.cs`.

- [ ] **Step 1: Create the folder via git mv** (no need to `mkdir` — git creates implicit folders on `git mv`).

```bash
cd /home/tim/source/ServiceConnect-CSharp
for pattern in "InMemory" "CacheProvider"; do
  for f in $(find src/ServiceConnect.UnitTests -maxdepth 1 -name "${pattern}*Tests.cs" -type f); do
    git mv "$f" "src/ServiceConnect.UnitTests/Persistence/InMemory/"
  done
done
```

- [ ] **Step 2: Update namespaces** to `ServiceConnect.UnitTests.Persistence.InMemory;`.

- [ ] **Step 3: Build + test.**

- [ ] **Step 4: Commit**

```
chore(tests): consolidate InMemory-persistence tests under Persistence/InMemory/

Moves 11 root-level tests covering InMemory aggregator / process-manager /
timeout-store implementations plus the cache provider into a new
Persistence/InMemory/ folder.
```

---

## Task 5: Move root-level Mongo tests into `Persistence/MongoDb/`

**Files:** ~24 files from root → `src/ServiceConnect.UnitTests/Persistence/MongoDb/`.

Files: all `Mongo*Tests.cs` (`MongoDbAggregatorPersistor*Tests.cs` (8), `MongoDbProcessManagerFinder*Tests.cs` (5), `MongoDbTimeoutStore*Tests.cs` (9), `MongoClientFactoryTests.cs`, `MongoClientFactoryCertCacheTests.cs`, `MongoClientFactoryCertCallbackTests.cs`).

The `Persistence/MongoDb/` folder already exists (the pre-release-fixes branch's Task 4 created `MongoDbPersistenceOptionsAggregatorLeaseTests.cs` there).

- [ ] **Step 1: Move** — `git mv ./Mongo*Tests.cs Persistence/MongoDb/`.

- [ ] **Step 2: Update namespaces** to `ServiceConnect.UnitTests.Persistence.MongoDb;`.

- [ ] **Step 3: Build + test.**

- [ ] **Step 4: Commit**

```
chore(tests): consolidate MongoDb-persistence tests under Persistence/MongoDb/

Moves 24 root-level MongoDB-related tests (aggregator persistor, process-
manager finder, timeout store, client factory + cert callback / cache) into
the existing Persistence/MongoDb/ folder.
```

---

## Task 6: Create `Configuration/` folder and move config-shape tests

**Files:** ~7 files → `src/ServiceConnect.UnitTests/Configuration/`.

Files: `QueueConfigurationTests.cs`, `QueueConfigurationCachedMappingsTests.cs`, `TransportConfigurationTests.cs`, `RequestOptionsTests.cs`, `SubConfigurationFreezeTests.cs`, `PersistenceConfigurationDefaultTests.cs`, `ConfigurationCleanupTests.cs`.

**Note about `BusConfigurationTests.cs`:** decide whether it belongs in `Configuration/` (testing the config shape) or `BusTests/` (testing config integration with the Bus). Read the file to determine — most likely shape-tests, so `Configuration/`.

- [ ] **Step 1: Create folder + move files.**
- [ ] **Step 2: Update namespaces** to `ServiceConnect.UnitTests.Configuration;`.
- [ ] **Step 3: Build + test.**
- [ ] **Step 4: Commit**

```
chore(tests): move configuration-shape tests into Configuration/ folder

7 tests covering QueueConfiguration, TransportConfiguration, RequestOptions,
PersistenceConfiguration, the SubConfigurationFreeze pattern, and config
cleanup move into a new Configuration/ folder for navigability.
```

---

## Task 7: Create `Builder/` folder and move builder-wiring tests

**Files:** ~4 files → `src/ServiceConnect.UnitTests/Builder/`.

Files: `ServiceConnectBuilderTests.cs`, `ServiceConnectBuilderPlaintextWarningTests.cs`, `ServiceCollectionExtensionsTests.cs`, `PersistenceRegistrationTests.cs`.

- [ ] **Step 1-4:** same shape as Task 6.

Commit message:
```
chore(tests): move builder/DI-registration tests into Builder/ folder

4 tests covering ServiceConnectBuilder, ServiceCollectionExtensions (DI
registration), and PersistenceRegistration move to a new Builder/ folder.
```

---

## Task 8: Move remaining root-level tests to existing small folders

**Files:** ~4 files distributed across already-existing folders.

- `HeaderDecoderTests.cs` → `Headers/`. Namespace: `ServiceConnect.UnitTests.Headers;`.
- `ExceptionShapeTests.cs`, `InterfaceCleanupTests.cs` → `Exceptions/`. Namespace: `ServiceConnect.UnitTests.Exceptions;`.
- `HandlerScannerTests.cs`, `HandlerScannerExceptionBreadthTests.cs`, `HandlerContextNullabilityTests.cs` → either `Handlers/` (already exists with 1 file) OR `Services/`. Decide by reading: if they test the discovery/scanning surface, `Handlers/`; if they test handler dispatch wiring, `Services/`. Recommend `Handlers/`.

- [ ] **Step 1: Move per the above mapping** (one `git mv` per pair).
- [ ] **Step 2: Update namespaces** per destination.
- [ ] **Step 3: Build + test.**
- [ ] **Step 4: Commit**

```
chore(tests): place remaining root-level tests in their topic folders

HeaderDecoderTests moves to Headers/, ExceptionShapeTests and
InterfaceCleanupTests move to Exceptions/, HandlerScanner+context tests
move to Handlers/. After this commit, only ModuleInit.cs and the two
xunit collection-fixture files remain at the project root.
```

---

## Task 9: Final cleanup

**Files:**
- Delete: empty `src/ServiceConnect.UnitTests/TestResults/` folder (if it's tracked).
- Modify: `.gitignore` (if `TestResults/` and `.trx` aren't already ignored at this level).

- [ ] **Step 1: Check `TestResults/` state**

```bash
cd /home/tim/source/ServiceConnect-CSharp
ls -la src/ServiceConnect.UnitTests/TestResults
git ls-files src/ServiceConnect.UnitTests/TestResults
```

If `git ls-files` returns lines, those files are tracked and should be removed. If the folder is empty and untracked, just leave it (and ensure `.gitignore` covers it for future use).

- [ ] **Step 2: Update `.gitignore`**

Check repo-root `.gitignore` for `TestResults/`. If absent, add a top-level entry:
```
TestResults/
*.trx
```

- [ ] **Step 3: Confirm the root is clean**

```bash
find src/ServiceConnect.UnitTests -maxdepth 1 -name '*.cs' -type f | sort
```

Expected: exactly 3 files — `ModuleInit.cs`, `MongoBsonSerialCollection.cs`, `SerialConcurrencyCollection.cs`.

- [ ] **Step 4: Build + final test sweep**

`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1`. All pass.

- [ ] **Step 5: Commit**

```
chore(tests): finalise UnitTests folder reorganisation

After Tasks 1-8, only ModuleInit and the xunit collection fixtures remain at
the test-project root. Ensures TestResults/ is gitignored at the repo level.
```

---

## Risk and mitigation notes

**Namespace shadowing — beyond `Bus`:**
- Verified before this plan: the only root-level production types under `ServiceConnect` are `Bus`, `ServiceConnectBuilder`, and `ServiceConnectLog`. The last two cannot collide with any sensible folder name. No other shadowing risks identified.
- If a future production type lands at `ServiceConnect.<NewType>` and a test folder name happens to match, the test build will catch it via CS0118.

**`InternalsVisibleTo` — no impact:**
- `ServiceConnect.UnitTests` has `InternalsVisibleTo` against `ServiceConnect`, `ServiceConnect.Client.RabbitMQ`, etc. Moving test files to subfolders does NOT change the assembly's identity; internal access continues to work.

**xUnit test discovery — no impact:**
- Test discovery walks the assembly, not the file system. Test methods retain their fully-qualified names; CI test filters (if any are pinned by FQN) may need updating, but local `dotnet test --filter` works identically.

**xUnit `[Collection]` attribute references:**
- `SerialConcurrencyCollection` and `MongoBsonSerialCollection` are referenced via `[Collection(SerialConcurrencyCollection.Name)]` — the reference is by typeof / static field, NOT by namespace. Keeping these two files at the project root means the references continue to resolve via `using ServiceConnect.UnitTests;` (which is the global root). If a moved test file's using-directive list does NOT explicitly include `ServiceConnect.UnitTests`, the static reference will fail to resolve — add the using directive when this happens.

**git rename detection:**
- `git mv` preserves rename history. The diff for each task should show "rename A → B" lines, NOT "delete A + add B". If git's rename detection fails (typically at ~50% similarity threshold), this means we accidentally edited content in the same commit as the move. Always do `git mv` first, then namespace edit, then a SEPARATE inspection of `git diff -M --stat` to confirm rename detection succeeded.

**File-name collisions (root → existing subfolder):**
- Task 3 explicitly checks for collisions before moving into `RabbitMQ/`. Apply the same defensive check to every task before `git mv` if you're worried about it — `for f in moves; do [ -f "$target/$(basename $f)" ] && echo COLLISION; done`.

---

## After all 10 tasks (Task 0 + 9 task commits)

The test project should have these top-level entries:
```
src/ServiceConnect.UnitTests/
├── ModuleInit.cs
├── MongoBsonSerialCollection.cs
├── SerialConcurrencyCollection.cs
├── ServiceConnect.UnitTests.csproj
├── xunit.runner.json (if present)
├── Aggregation/
├── Builder/                 ← NEW
├── BusTests/                ← renamed from Bus/
├── Configuration/           ← NEW
├── DI/
├── Diagnostics/
├── Events/
├── Exceptions/
├── Fakes/
├── Handlers/
├── Headers/
├── HealthChecks/
├── Messages/
├── Options/
├── Persistence/
│   ├── InMemory/            ← NEW
│   └── MongoDb/
├── Processors/
├── RabbitMQ/
├── Services/
├── Telemetry/
└── Timeouts/
```

No `.cs` files in the root other than the three above. Every test class lives under a topic folder.

**Total commits:** 10 (Task 0 + Tasks 1-9).

**Total file moves:** ~106.

**Diff per commit:** typically 5-25 files per commit — large but reviewable. Git rename detection keeps the diff manageable (line-level changes are only the `namespace` declaration per file).

**Alternative — collapse into fewer commits:** if you'd rather have, say, 3 commits ("Bus + Services", "Persistence + RabbitMQ", "everything else + cleanup"), the same plan still applies, just batched. Pure mechanical change.
