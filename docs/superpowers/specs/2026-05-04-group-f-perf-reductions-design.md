# Group F — Perf reductions — design

**Status:** approved, awaiting implementation plan
**Source review:** [architecture-review-deep.md](../../../architecture-review-deep.md)
**Roadmap:** [architecture-fix-plan.md](../../../architecture-fix-plan.md) — Group F
**Date:** 2026-05-04

---

## Overview

Group F shaves allocations on two hot paths. Both items are mechanical, internal-only, and target redundant work that profiling would surface as low-hanging fruit:

- **Item 3 (in scope as Task 1):** Eager-decode `byte[]` headers to `string` at the inbound copy site. The existing inbound copy loop already iterates each header; replacing the raw bytes with their UTF8-decoded string lets every subsequent `HeaderDecoder.Decode(headers[key])` call across the dispatcher, processors, telemetry, filters, middleware, and user handlers hit the existing `if (value is string)` fast-path and skip the redundant decode.

- **Item 4 (in scope as Task 2):** Eliminate `OutboundHeaderBuilder.BuildBasicProperties`'s double-copy. The current code allocates a fresh `Dictionary<string, object?>` and copies every entry from a `Dictionary<string, object>` purely to align value-type nullability with `BasicProperties.Headers`. Widening `BuildHeaders` return type and `BuildBasicProperties` parameter to `Dictionary<string, object?>` lets the source dict assign directly to `BasicProperties.Headers` — one alloc + one copy loop saved per publish.

The roadmap originally framed Group F with four items. Items 1 (centralise `CopyInboundHeaders`) and 2 (skip `ExtractHeaders` on no-mutation) are out of scope here:

- **Item 1** was framed as an allocation win in the roadmap, but the host's failure branches are mutually exclusive — each branch returns, so only one ever fires per delivery. Hoisting to a shared site doesn't reduce per-delivery allocations. The genuine duplication is between `RabbitMqConsumerHost.CopyInboundHeaders` and `InboundMessageProcessor.ProcessAsync`'s identical copy loop — a DRY refactor, not a perf win. Recorded in `notes.md` as a follow-up alongside the next `RabbitMqConsumerHost.cs` split pass.

- **Item 2** would require either an instrumented mutation-tracking wrapper around `Envelope.Headers` or breaking the public `IDictionary<string,object>` contract. Detection cost outweighs the saved alloc on typical workloads. Recorded in `notes.md` as a profile-driven follow-up.

### Cross-cutting decisions banked from brainstorm

- **No new public-API surface.** Both items are internal type-system tightening or hot-path optimisations. `IConsumeContext` does NOT gain a `GetDecodedHeader` method (option B from Question 2 was rejected — option A's eager-decode is cleaner and zero-API-change).
- **Eager-decode targets `byte[]` only.** Typed values (bool, enum, `IDictionary`, `IEnumerable`) stay on-demand. The `HeaderDecoder.Render` path for typed values is more expensive but eagerly invoking it would defeat the purpose for headers nothing reads.
- **No benchmarks.** The savings are visible to an allocation profiler but synthetic `BenchmarkDotNet` runs at sub-microsecond grain are noise. Reviewer-visible diff + documented invariants are sufficient.
- **Three atomic commits.** Two perf commits (one per item) plus the roadmap close-out, mirroring Groups A/C/D structure.

---

## In scope

| # | Change | Where | Driver |
|---|---|---|---|
| 1a | Replace `byte[]` values with their UTF8-decoded `string` form during the inbound copy loop | [RabbitMqConsumerHost.cs:610](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L610) `CopyInboundHeaders` | Eliminate redundant decodes downstream |
| 1b | Same change in `InboundMessageProcessor.ProcessAsync`'s inline copy loop | [InboundMessageProcessor.cs:57-69](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L57) | Same; this is the dispatch-path equivalent |
| 1c | Unit test asserting eager-decode substitution + downstream identity-equality | New: `src/ServiceConnect.UnitTests/RabbitMQ/InboundHeaderDecodeCachingTests.cs` | Regression guard |
| 2a | `OutboundHeaderBuilder.BuildHeaders` return type widens from `Dictionary<string, object>` to `Dictionary<string, object?>` | [OutboundHeaderBuilder.cs:49](../../../src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L49) | Type alignment with `BasicProperties.Headers` |
| 2b | `BuildBasicProperties` parameter widens to `Dictionary<string, object?>`; copy loop deleted; `BasicProperties.Headers` assigned directly | [OutboundHeaderBuilder.cs:99-106](../../../src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L99) | Eliminate the redundant copy |
| 2c | Unit test asserting `Object.ReferenceEquals(messageHeaders, basicProperties.Headers)` | New: `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderAliasingTests.cs` | Proves the copy is gone |

## Out of scope (deliberately)

- **Item 1 (centralise `CopyInboundHeaders`).** No real allocation win once the failure-branch math is honest. DRY refactor recorded in `notes.md`.
- **Item 2 (skip `ExtractHeaders` on no-mutation).** Detection cost > savings. Recorded in `notes.md`.
- **Public-API additions.** No `IConsumeContext.GetDecodedHeader` method. Option B from the brainstorm was rejected.
- **Eager-decode for typed values.** `bool`, `enum`, `IDictionary`, `IEnumerable` stay on-demand. `HeaderDecoder.Render` keeps its existing semantics.
- **Synthetic benchmarks.** Sub-microsecond allocation savings are noise in `BenchmarkDotNet`; visible only in allocation profilers, which operators run on their own deployments.
- **Cross-package consumers of `OutboundHeaderBuilder`.** None exist — the type is `internal sealed class`. No `[Obsolete]` shim or transitional helper needed.

---

## Item 3 (Task 1) — Eager-decode at inbound copy

### 1a. `RabbitMqConsumerHost.CopyInboundHeaders`

Open `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`. The existing copy at line 610-626:

```csharp
private static Dictionary<string, object> CopyInboundHeaders(BasicDeliverEventArgs args)
{
    var sourceHeaders = args.BasicProperties.Headers;
    var headers = new Dictionary<string, object>((sourceHeaders?.Count ?? 0) + 1, StringComparer.Ordinal);
    if (sourceHeaders != null)
    {
        foreach (var kvp in sourceHeaders)
        {
            if (kvp.Value is not null)
            {
                headers[kvp.Key] = kvp.Value;
            }
        }
    }

    return headers;
}
```

Replace the inner `if (kvp.Value is not null)` body with the eager-decode branch:

```csharp
foreach (var kvp in sourceHeaders)
{
    if (kvp.Value is null)
    {
        continue;
    }

    // Eagerly decode the AMQP wire-format byte[] to its UTF8 string. Most headers are
    // read at least once downstream (telemetry, dispatcher, audit), so the eager decode
    // pays for itself by short-circuiting every future HeaderDecoder.Decode call to the
    // existing string-fast-path. Typed values (bool, int, IDictionary, IEnumerable)
    // stay as objects so HeaderDecoder.Render still handles them on demand.
    headers[kvp.Key] = kvp.Value is byte[] bytes
        ? Encoding.UTF8.GetString(bytes)
        : kvp.Value;
}
```

Add `using System.Text;` if not already present.

### 1b. `InboundMessageProcessor.ProcessAsync`'s inline copy

Open `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs`. The existing inline copy at line 57-69:

```csharp
var sourceHeaders = args.BasicProperties.Headers;

var headers = new Dictionary<string, object>((sourceHeaders?.Count ?? 4) + 3, StringComparer.Ordinal);
if (sourceHeaders != null)
{
    foreach (var kvp in sourceHeaders)
    {
        if (kvp.Value is not null)
        {
            headers[kvp.Key] = kvp.Value;
        }
    }
}
```

Apply the same eager-decode replacement to the inner body:

```csharp
foreach (var kvp in sourceHeaders)
{
    if (kvp.Value is null)
    {
        continue;
    }
    headers[kvp.Key] = kvp.Value is byte[] bytes
        ? Encoding.UTF8.GetString(bytes)
        : kvp.Value;
}
```

Add `using System.Text;` if not already present.

### 1c. Unit test

Create `src/ServiceConnect.UnitTests/RabbitMQ/InboundHeaderDecodeCachingTests.cs`. Three facts:

1. **`CopyInboundHeaders_ReplacesByteArrayValuesWithStrings`** — build a `BasicDeliverEventArgs` with one `byte[]` header (UTF8 of "test-value") and one typed header (e.g. `(int)42`). After `CopyInboundHeaders` runs (via `RabbitMqConsumerHost.CopyInboundHeadersForTests` if `internal`-friendly access is needed; otherwise drive through the consumer host pipeline). Assert the byte[] entry is now `string` "test-value" and the int entry is unchanged at `42`.

2. **`HeaderDecoder_Decode_ReturnsIdentityEqualString_AfterEagerDecode`** — given the dictionary produced in test 1, call `HeaderDecoder.Decode(dict["test-key"])` twice. Assert `Object.ReferenceEquals(first, second)` and that both equal `"test-value"`. Proves the string-fast-path is hit and no re-allocation occurs.

3. **`InboundMessageProcessor_ProcessAsync_AppliesSameEagerDecode`** — drive a message through `InboundMessageProcessor.ProcessAsync` with a `byte[]` header and assert via a stub handler that the header arrives at the handler as a `string` not a `byte[]`. Confirms 1b's parallel change.

Reuse the consumer host / inbound-processor test scaffolding from existing tests in `src/ServiceConnect.UnitTests/RabbitMQ/`. If `CopyInboundHeaders` is `private static`, expose via `[InternalsVisibleTo]`+`internal` test-access helper (matches Group D's `ProducerConnection.TestAccess` pattern), or drive through the public path.

---

## Item 4 (Task 2) — Eliminate `OutboundHeaderBuilder` double-copy

### 2a. Widen `BuildHeaders` return type

Open `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs`. The existing signature at line 49:

```csharp
public Dictionary<string, object> BuildHeaders(Type type, IReadOnlyDictionary<string, string>? headers, string queueName, string messageType)
```

Becomes:

```csharp
public Dictionary<string, object?> BuildHeaders(Type type, IReadOnlyDictionary<string, string>? headers, string queueName, string messageType)
```

The internal `result` field also widens:

```csharp
var result = new Dictionary<string, object?>(estimatedCount, StringComparer.Ordinal);
```

The body's stamping logic (`result[HeaderKeys.X] = value;`) stays unchanged because `string` (and any reference type) widens implicitly to `object?`.

### 2b. Widen `BuildBasicProperties` parameter and delete the copy loop

The existing method at lines 99-117:

```csharp
public BasicProperties BuildBasicProperties(Dictionary<string, object> messageHeaders)
{
    // foreach avoids the LINQ Select + enumerator allocation per message.
    var headersCopy = new Dictionary<string, object?>(messageHeaders.Count, StringComparer.Ordinal);
    foreach (var kvp in messageHeaders)
    {
        headersCopy[kvp.Key] = kvp.Value;
    }

    var basicProperties = new BasicProperties
    {
        Headers = headersCopy,
        Persistent = true
    };

    if (messageHeaders.TryGetValue(HeaderKeys.MessageId, out var messageId))
    {
        basicProperties.MessageId = messageId?.ToString();
    }
    // ... priority, etc.
}
```

Becomes:

```csharp
public BasicProperties BuildBasicProperties(Dictionary<string, object?> messageHeaders)
{
    // Direct assign — type alignment with BasicProperties.Headers (IDictionary<string, object?>)
    // is now native, so no copy is needed. Producer.cs callers don't mutate messageHeaders
    // after this call (BuildBasicProperties is the last touch before PublishWithTimeoutAsync),
    // so the alias from BasicProperties.Headers back to messageHeaders is benign.
    var basicProperties = new BasicProperties
    {
        Headers = messageHeaders,
        Persistent = true
    };

    if (messageHeaders.TryGetValue(HeaderKeys.MessageId, out var messageId))
    {
        basicProperties.MessageId = messageId?.ToString();
    }
    // ... rest unchanged
}
```

### 2c. Caller compatibility

Four call sites in `Producer.cs`: lines 182, 251, 331, 394. Each does `var messageHeaders = _headerBuilder.BuildHeaders(...)`. The `var` infers the new type. Subsequent reassignments like `baseHeaders[HeaderKeys.DestinationAddress] = endPoint;` still compile because `string` widens implicitly to `object?`. **No call-site edits needed.**

`OutboundHeaderBuilder` is `internal sealed class` — no out-of-package consumers. Verified by `grep` at the brainstorm stage.

### 2d. Aliasing verification (load-bearing)

After the change, `BasicProperties.Headers` and the producer's local `messageHeaders` reference the same dictionary. The producer must NOT mutate `messageHeaders` after `BuildBasicProperties` returns, or RabbitMQ.Client could observe a half-mutated dict.

The implementation plan includes a verification step: re-read `Producer.PublishAsync`, `SendAsync`, `SendBytesAsync` from after the call to `BuildBasicProperties` through to the call to `PublishWithTimeoutAsync` and confirm no `messageHeaders[…] = …` or `messageHeaders.Add(…)` happens on that span. The current code (lines 182-192, 250-292, 330-360, 394-420) does NOT mutate after `BuildBasicProperties` — confirmed at the brainstorm stage. The plan re-confirms in case prior groups shifted line numbers.

### 2e. Unit test

Create `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderAliasingTests.cs`:

```csharp
[Fact]
public void BuildBasicProperties_DoesNotCopyMessageHeaders()
{
    var builder = new OutboundHeaderBuilder(/* construct with minimal deps */);
    var headers = builder.BuildHeaders(typeof(SampleMessage), null, "queue-name", "Publish");

    var basicProperties = builder.BuildBasicProperties(headers);

    Assert.Same(headers, basicProperties.Headers);
}
```

The `Assert.Same` check is the regression guard — if a future change reintroduces the copy, the test fails.

Existing producer publish/send tests stay green (publish behaviour unchanged).

---

## Testing strategy

| Item | Verification |
|---|---|
| 1a, 1b (eager-decode) | New `InboundHeaderDecodeCachingTests.cs` — 3 facts. Existing inbound-pipeline tests stay green (assertions go through `HeaderDecoder.Decode`). |
| 1c (downstream identity) | Same test file. `Object.ReferenceEquals` assertion proves the cache is hit. |
| 2a, 2b (type widening + copy elimination) | New `OutboundHeaderBuilderAliasingTests.cs` — 1 fact. Existing producer tests stay green (publish behaviour unchanged). |
| Build verification | `dotnet build src/ServiceConnect.slnx -m:1` clean. The four `Producer.cs` call sites compile against the widened return type via `var` inference. |
| End-to-end | `dotnet build src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1` clean. The E2E test fixtures construct headers as `byte[]` (RabbitMQ wire format) and expect them to pass through to handlers; eager-decode means handlers receive `string` instead, which they already do via `HeaderDecoder.Decode` so the assertions still hold. |

No new test infrastructure beyond what Group B already added.

---

## Rollout

Three atomic commits on `v7-clean-architecture` (mirrors Group D structure):

1. **`perf(transport): eager-decode inbound byte[] headers to string`** — Item 3. Touches `RabbitMqConsumerHost.CopyInboundHeaders` and `InboundMessageProcessor.ProcessAsync`'s inline copy. New `InboundHeaderDecodeCachingTests.cs`.
2. **`perf(transport): eliminate OutboundHeaderBuilder double-copy`** — Item 4. Widens `BuildHeaders` return type and `BuildBasicProperties` parameter; deletes the copy loop. New `OutboundHeaderBuilderAliasingTests.cs`.
3. **`docs(architecture): mark Group F done in the fix plan`** — close-out.

Each commit independently passes build + tests. Reverting any one cleanly removes that increment without affecting the others. No public-API surface change.

---

## Risks

- **Aliasing in item 4.** After the change, `BasicProperties.Headers` and the producer's local `messageHeaders` reference the same dictionary. Mitigation: producer doesn't mutate `messageHeaders` after `BuildBasicProperties` returns; verified at brainstorm stage; plan re-confirms at implementation time. If a future change adds post-`BuildBasicProperties` mutation, the new test won't catch it (test is on the immediate aliasing, not on mutation discipline) — flag in the test's docstring as a maintenance hazard.
- **Eager-decode cost for unread headers.** Item 3 eagerly decodes every `byte[]` header. Worst case: 2-3 unread headers × ~50ns each = ~150ns wasted per delivery. Negligible against the 5-7 redundant decodes saved on read-multiple headers. Documented in the implementation comment.
- **`InMemoryTimeoutStoreTests` byte[] assertions.** Verified at brainstorm stage that those tests are on the timeout-store path (`TimeoutEntry.Headers`), NOT the AMQP consume path. Eager-decode doesn't reach there.
- **Type widening cascading.** Theoretically, widening `BuildHeaders` return type could break callers that rely on `Dictionary<string, object>` non-nullability. Verified at brainstorm: only four `Producer.cs` call sites, all using `var`. No assignments to typed variables that would surface the widening as a compile error.
- **Rollback** — both perf commits are pure mechanical changes with no data migrations or API breaks. `git revert` on either independently restores the prior allocation pattern; nothing else has to come along.

---

## Decisions banked from the brainstorm

For audit trail:

- **Items 1 and 2 deferred** to `notes.md` follow-ups. Reasoning: item 1 has no real allocation win (failure branches mutually exclusive); item 2's detection cost > savings.
- **Eager-decode targets `byte[]` only** (option A from Question 2). Typed values stay on-demand. No `IConsumeContext.GetDecodedHeader` method (option B rejected).
- **Aliasing in item 4 is benign** because producers don't mutate `messageHeaders` post-`BuildBasicProperties`. Verified by reading the publish/send/sendBytes paths.
- **No benchmarks** — savings are profiler-visible at sub-microsecond grain, below `BenchmarkDotNet` noise floor.
- **Three atomic commits** — two perf commits + roadmap close-out, mirroring Groups A/C/D.
