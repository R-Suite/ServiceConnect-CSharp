# CustomFilterAndMiddleware sample

Demonstrates the two ServiceConnect extension points that let you customise
the consume pipeline: **filters** (envelope-level pre/post hooks) and
**message-processing middleware** (handler-wrapping middleware that observes
the deserialised message).

The worked scenario is broker-redelivery deduplication — the canonical use
case for the on-success filter stage. Implementing dedupe correctly requires
two filters, not one:

- A `BeforeConsumingFilter` that consults a persistor and short-circuits when
  the `MessageId` is already recorded.
- An `OnConsumedSuccessfullyFilter` that records the `MessageId` **only** if
  the handler completed successfully. Recording before the handler runs (or
  in an `AfterConsumingFilter`, which runs on both success and failure paths)
  silently drops legitimate broker redeliveries after a handler crash.

## How to run

Requires Docker + .NET 10 SDK.

```bash
./run.sh
```

This starts RabbitMQ in a container, builds the sender + consumer, runs the
consumer in the background, runs the sender, sleeps a few seconds, kills the
consumer, and prints the consumer log.

## What you should see

The sender publishes three messages: `order-1`, `crash-once`, `order-2`.
The consumer's log shows:

- `LoggingTimingMiddleware` printing `→` and `←` markers around each handler
  invocation with elapsed time.
- `crash-once` is delivered twice: the first attempt throws (the middleware
  prints `THREW`), the broker redelivers, the second attempt succeeds. The
  on-success filter records the id only on the second attempt.
- The `DedupeIncomingFilter` logs nothing because none of the three sender
  messages is a redelivery of an *already-recorded* id (the handler's first
  attempt at `crash-once` failed, so the id was not recorded — the redelivery
  proceeds).

To see the BeforeConsuming filter actually block a duplicate, manually run
`./run.sh` twice without restarting the consumer container — the second run's
sender will publish messages whose ids have already been recorded by the
first run's consumer (subject to consumer process restart caveats; see below).

## Filter walkthrough

`Filters/IDedupePersistor.cs` — the contract the sample's two filters share.
The atomic `TryInsertAsync` returns true if the id was new, false if a
concurrent caller already recorded it. This atomicity is the whole point: a
read-then-write pattern (`ContainsAsync` then `InsertAsync`) admits two
concurrent deliveries past the existence check before either records,
defeating dedupe under contention.

`Filters/InMemoryDedupePersistor.cs` — a per-process implementation backed by
`ConcurrentDictionary.TryAdd`. Suitable for the sample only; does not survive
process restart and does not coordinate across replicas.

`Filters/DedupeIncomingFilter.cs` — `BeforeConsuming` filter. Reads the
`MessageId` header, consults the persistor, returns `Stop` if present.

`Filters/DedupeOnSuccessFilter.cs` — `OnConsumedSuccessfully` filter. Records
the id atomically. If the persistor reports the id was already present (a
race past the BeforeConsuming check), throws — the dispatcher returns
`Success=false` so the broker redelivers and the next attempt's
BeforeConsuming filter blocks.

## Middleware walkthrough

`Middleware/LoggingTimingMiddleware.cs` — wraps every handler invocation with
entry/exit log lines and a `Stopwatch`. Middleware differs from filters in
two ways:

1. Middleware is **inside** the dispatch — it sees the deserialised message
   instance, not just the envelope.
2. Middleware uses `next(...)` to invoke the next stage explicitly, allowing
   pre- and post-handler logic in a single class. Filters short-circuit by
   returning `FilterAction.Stop`; middleware short-circuits by not calling
   `next`.

Reach for middleware when you want to wrap the handler call with timing,
tracing, or transactional scoping. Reach for a filter when you want to make
an admission decision based on the envelope or its headers.

## Production caveats

The toy `InMemoryDedupePersistor` is **not** production-ready:

- It does not survive process restart. A consumer pod that's killed mid-flight
  loses every recorded id.
- It does not coordinate across consumer replicas. Two consumers behind the
  same queue will each maintain their own dictionary.
- It has no expiry/eviction. Memory grows without bound.

For real workloads, implement `IDedupePersistor` against a shared store with
an atomic insert primitive. Sketch for MongoDB:

```csharp
public sealed class MongoDedupePersistor(IMongoCollection<ProcessedMessage> col) : IDedupePersistor
{
    public async Task<bool> ContainsAsync(Guid id, CancellationToken ct = default)
        => await col.Find(p => p.Id == id).AnyAsync(ct);

    public async Task<bool> TryInsertAsync(Guid id, DateTime expiry, CancellationToken ct = default)
    {
        try
        {
            await col.InsertOneAsync(new ProcessedMessage { Id = id, Expiry = expiry }, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }
}
```

Pair this with a unique index on `_id` and a TTL index on `Expiry`. The TTL
index handles cleanup; no separate cleanup hosted service needed.

Handler-side idempotency remains the canonical answer where it's available
(idempotent business operations, natural upsert keys). The filter pattern
above is a belt-and-braces layer for handlers whose side effects can't be
made idempotent at the business level.

See also: [Idempotency](https://github.com/R-Suite/ServiceConnect-CSharp/blob/master/website/src/content/docs/learn/operations/idempotency.mdx).
