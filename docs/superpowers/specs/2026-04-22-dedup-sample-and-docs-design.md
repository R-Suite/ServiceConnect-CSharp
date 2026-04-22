# Message Deduplication Sample and Undocumented Extension Points: Docs + Sample

**Date:** 2026-04-22
**Status:** Approved
**Owner:** Tim Watson
**Branch:** `v7-clean-architecture` (existing)

## Context

An audit of ServiceConnect-CSharp against the website docs identified five features that exist in code but are not documented on the website (polymorphic messaging, which was covered by a separate spec on the same date, is excluded here):

1. `ServiceConnect.Filters.MessageDeduplication` — production-ready filter package with In-Memory and MongoDB persistors, a DI extension, and a background cleanup hosted service. Not documented. No runnable sample.
2. `IRequestReplyManager` — request/reply correlation tracking extension point. Not documented.
3. `IRegistryInitializer` — handler-registry bootstrap extension point. Not documented.
4. `ITimeoutStore` — base timeout store interface. Only its `ILeaseAwareTimeoutStore` variant has a reference page.
5. No `examples/MessageDeduplication/` sample; all other messaging-adjacent features have one.

This spec addresses all five gaps in a single change set on the existing `v7-clean-architecture` branch.

## Goals

1. Add a runnable `examples/MessageDeduplication/` sample that demonstrates duplicate filtering end-to-end against a shared MongoDB persistor.
2. Add a dedicated reference page for the `MessageDeduplication` filter package.
3. Add a short `learn/operations/idempotency.mdx` page covering the operational motivation, linking to the filter reference page and the sample.
4. Add reference pages for `IRequestReplyManager`, `IRegistryInitializer`, and `ITimeoutStore`.
5. Wire the new pages into `astro.config.mjs` sidebar navigation and add the new sample to `samples.mdx`.

## Non-goals

- Changes to the filter package source or its existing unit tests. The filter works; this is a documentation + sample change.
- Automated integration tests for the sample. Existing examples are not automated; this one follows that convention.
- Changes to `IRequestReplyManager`, `IRegistryInitializer`, or `ITimeoutStore` themselves. Reference pages describe behaviour as-is.
- A separate "messaging pattern" page for deduplication. Idempotency is an operations concern, not a pattern. One operations page is enough.
- A new top-level sidebar group. The new pages slot into existing groups (`reference/filters`, `reference/extension-points/*`, `learn/operations`).

## Deliverables

### Sample project

A new sample at `examples/MessageDeduplication/`, mirroring the shape of `examples/Filters/`:

```
examples/MessageDeduplication/
├── MessageDeduplication.sln
├── appsettings.json
├── docker-compose.yml
├── run.sh
├── run.ps1
├── README.md
└── src/
    ├── ServiceConnect.Examples.MessageDeduplication.Contracts/
    │   └── OrderPlaced.cs
    ├── ServiceConnect.Examples.MessageDeduplication.Consumer/
    │   ├── Program.cs
    │   └── OrderPlacedHandler.cs
    └── ServiceConnect.Examples.MessageDeduplication.Sender/
        └── Program.cs
```

**Scenario.** Both the Sender and Consumer register `AddMessageDeduplicationFilter` pointing at the same MongoDB database. Filter wiring: Sender uses `builder.AddOutgoingFilter<OutgoingDeduplicationFilter>()`; Consumer uses `builder.AddBeforeConsumingFilter<IncomingDeduplicationFilter>()`. The Sender publishes one `OrderPlaced`; the `OutgoingDeduplicationFilter` records its `MessageId` in Mongo. The Consumer's handler logs `Handled OrderPlaced <id>` and then throws (simulating a crash after the side effect) — this causes the broker to redeliver the message with `Redelivered=true`. On the redelivery, the `IncomingDeduplicationFilter` sees the `MessageId` already in Mongo and blocks it. The demonstration is: `Handled OrderPlaced` appears exactly once in the Consumer log; a second line (e.g. `Redelivered filtered <id>`) confirms the block.

**Why MongoDB, not InMemory.** The `IncomingDeduplicationFilter` only blocks when the `MessageId` is present in the persistor, and the `OutgoingDeduplicationFilter` (on the Sender) is what inserts it. In a two-process Sender/Consumer topology, the persistor must be shared — which rules out the InMemory persistor (each process has its own dictionary). MongoDB is the only persistor that demonstrates the feature end-to-end across processes. The reference page documents InMemory's valid single-process use cases; the sample does not.

**appsettings.json.** Minimal — just enough to drive the filter:

```json
{
  "Deduplication": {
    "PersistorType": "MongoDb",
    "ConnectionStringMongoDb": "mongodb://localhost:27017",
    "DatabaseNameMongoDb": "dedup-sample",
    "CollectionNameMongoDb": "ProcessedMessages",
    "MsgExpiryHours": 24,
    "MsgCleanupIntervalMinutes": 60
  }
}
```

Field names match `DeduplicationFilterSettings` exactly (verified against [`DeduplicationFilterSettings.cs`](../../../filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationFilterSettings.cs)). `MsgExpiryHours` is set to 24 for realism; a long retention matches how the filter would be configured in practice. The cleanup interval is kept default.

**Infrastructure.** `docker-compose.yml` runs RabbitMQ and MongoDB with healthchecks. `run.sh` / `run.ps1` start docker-compose, wait for both services to be healthy, launch the Consumer in the background, wait for its ready marker, run the Sender (which publishes once and exits), give the Consumer a few seconds to process both the initial delivery and the redelivery, then stop the Consumer and tear down docker. The README documents expected log output.

**Solution file.** A dedicated `examples/MessageDeduplication/MessageDeduplication.sln` containing only the three sample projects, matching how `examples/Filters/Filters.sln` is structured.

### Documentation pages

**1. `website/src/content/docs/reference/filters/messagededuplication.mdx`** — estimated 8–10 KB, matching the depth of [`ifilter.mdx`](../../../website/src/content/docs/reference/filters/ifilter.mdx). Sections:

- Title frontmatter + "When to use" opener
- Package install (`dotnet add package ServiceConnect.Filters.MessageDeduplication`)
- DI registration with `AddMessageDeduplicationFilter(configure)`
- `DeduplicationFilterSettings` reference — the actual field names (verified against source): `PersistorType`, `MsgExpiryHours`, `MsgCleanupIntervalMinutes`, `ConnectionStringMongoDb`, `DatabaseNameMongoDb`, `CollectionNameMongoDb`, `DisableMsgExpiry`, plus the TLS/cert fields (`MongoDbCertPath`, `MongoDbCertBase64`, `MongoDbCertPassphrase`)
- Persistor selection: InMemory vs MongoDB tradeoffs (process-local vs shared across replicas; crash durability)
- Incoming vs Outgoing filter behaviour
- `DeduplicationCleanupHostedService` — expiry, retention semantics, interval tuning
- Writing a custom `IMessageDeduplicationPersistor` (short — points at interface source)
- Sample link

**2. `website/src/content/docs/learn/operations/idempotency.mdx`** — estimated 4–6 KB. Sections:

- At-least-once delivery as a ServiceConnect baseline; when duplicates occur (broker redelivery, handler crash after side-effect)
- Handler-side idempotency (domain-specific dedup keys, natural upserts) vs infrastructure dedup (the filter)
- When each approach fits; when they combine
- Pointer to the filter reference page and the new sample

**3. `website/src/content/docs/reference/extension-points/persistence/itimeoutstore.mdx`** — estimated 8–10 KB, matching the depth of the sibling [`ileaseawaretimeoutstore.mdx`](../../../website/src/content/docs/reference/extension-points/persistence/ileaseawaretimeoutstore.mdx). Sections:

- Role: base interface for deferred/scheduled message storage
- Interface member reference with semantics (verified against [`ITimeoutStore.cs`](../../../src/ServiceConnect.Interfaces/Persistence/ITimeoutStore.cs))
- Relationship to `ILeaseAwareTimeoutStore` (when leases are required, e.g. clustered setups)
- Default implementation pointer
- Back-link added from `ileaseawaretimeoutstore.mdx` to this page

**4. `website/src/content/docs/reference/extension-points/registry/iregistryinitializer.mdx`** — estimated 4–6 KB. Sections:

- Role: hook called during handler-registry bootstrap
- When you would implement one (custom assembly scanning, handler filtering)
- Interface contract (verified against [`RegistryInitializer.cs`](../../../src/ServiceConnect/Services/RegistryInitializer.cs))
- Registration example

**5. `website/src/content/docs/reference/extension-points/bus/irequestreplymanager.mdx`** — estimated 6–8 KB. Located in a new `bus/` subfolder under `extension-points/` (mirrors the existing top-level `reference/bus/` grouping; does not require inventing a "messaging" subfolder). Sections:

- Role: tracks in-flight request/reply correlations
- Where it sits in the flow (relationship to `IBus.SendRequest`-style APIs)
- Interface members (verified against [`RequestReplyManager.cs`](../../../src/ServiceConnect/Services/RequestReplyManager.cs) and [`IReplyStatusRequestReplyManager.cs`](../../../src/ServiceConnect/Services/IReplyStatusRequestReplyManager.cs))
- `IReplyStatusRequestReplyManager` variant — short section, cross-links
- Registration example

### Navigation and catalog

- `website/astro.config.mjs` — add five sidebar entries:
  - Under `reference/filters/`: "Message Deduplication"
  - Under `reference/extension-points/persistence/`: "ITimeoutStore"
  - Under `reference/extension-points/registry/`: "IRegistryInitializer"
  - New `reference/extension-points/bus/` group with: "IRequestReplyManager"
  - Under `learn/operations/`: "Idempotency"
- `website/src/content/docs/samples.mdx` — add a row for "Message Deduplication" linking to `examples/MessageDeduplication/` and the new reference page.

## Verification

**Sample.** Implementer runs the sample end-to-end with docker-compose and confirms the Consumer log contains exactly one `Handled OrderPlaced` line plus the redelivery-blocked marker. Expected log output is captured in the README.

**Docs.** `npm run build` in `website/` must succeed — Astro errors on broken internal links, malformed frontmatter, and unresolved sidebar references, so this is the gating check. `npm run dev` spot-check confirms sidebar entries appear in the expected groups.

**Interface accuracy.** For each new reference page, signatures and member semantics are checked line-by-line against the `.cs` source files listed above. No paraphrasing.

## Git workflow

Work lands as a series of commits on `v7-clean-architecture` (no new branch). Suggested commit boundaries, in order:

1. `feat(samples): add MessageDeduplication sample with switchable persistor`
2. `docs(website): document MessageDeduplication filter and idempotency`
3. `docs(website): document ITimeoutStore, IRegistryInitializer, IRequestReplyManager`
4. `docs(website): wire new pages into sidebar and samples catalog`

Separating sample from docs keeps reviewable diffs manageable; separating the filter docs from the extension-point docs keeps each commit thematically tight.

## Risks and open questions

- **Redelivery trigger** — the sample relies on the Consumer handler throwing to cause a broker NACK-and-redeliver with `Redelivered=true`. This depends on ServiceConnect's default NACK behaviour keeping the message on the queue (not dead-lettering it). Implementer verifies this end-to-end during sample run; if default behaviour dead-letters instead, switch to an explicit `bus.PublishAsync(..., new PublishOptions { Headers = { ["Redelivered"] = "True" } })` second-send from the Sender — matching the pattern in [`MessageDeduplicationTests.cs`](../../../src/ServiceConnect.EndToEndTests/Filters/MessageDeduplicationTests.cs). Both scenarios exercise the filter; the thrown-handler path is preferred because it reflects the real operational case.
- **Docker-compose readiness** — `run.sh` must wait for RabbitMQ *and* MongoDB to be healthy before launching the Consumer. Implementer mirrors the `wait_for_ready` pattern from [`examples/Aggregator/run.sh`](../../../examples/Aggregator/run.sh) and uses `depends_on.condition: service_healthy` in docker-compose.
- **Sidebar group ordering** — where the new `bus/` subfolder under `extension-points/` sits in the sidebar order should match the existing alphabetical or semantic ordering visible in `astro.config.mjs`. Implementer matches the existing convention; this is not a new decision.
