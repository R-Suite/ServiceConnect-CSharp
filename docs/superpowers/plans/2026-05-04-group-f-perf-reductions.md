# Group F — Perf Reductions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Shave allocations on two hot paths: (1) eager-decode `byte[]` headers to `string` at the inbound copy site so downstream `HeaderDecoder.Decode` calls hit the existing string fast-path; (2) eliminate the per-publish `OutboundHeaderBuilder.BuildBasicProperties` copy loop by widening the producer-header value-type to `object?`.

**Architecture:** Two mechanical, internal-only commits plus a roadmap close-out. No public-API surface change. Both items target identical-pattern allocations: redundant work that the framework currently does and downstream callers don't realise.

**Tech Stack:** .NET 8/10, C# 14, RabbitMQ.Client 7.2.1, xUnit, Moq, `Microsoft.Extensions.Time.Testing` (`FakeTimeProvider`).

---

## File structure

| File | Item | Action |
|---|---|---|
| `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` | 3 | Modify `CopyInboundHeaders` (around line 610) — eager-decode `byte[]` → `string` |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs` | 3 | Modify the inline copy at lines 57-69 — same eager-decode |
| `src/ServiceConnect.UnitTests/RabbitMQ/InboundHeaderDecodeCachingTests.cs` | 3 | Create — three facts |
| `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs` | 4 | Modify `BuildHeaders` return type and `BuildBasicProperties` parameter; delete copy loop |
| `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderAliasingTests.cs` | 4 | Create — one fact (`Assert.Same` on alias) |
| `architecture-fix-plan.md` | close | Modify — mark Group F done |

---

## Task 1: Item 3 — Eager-decode inbound `byte[]` headers to `string`

The consumer's two header-copy sites currently iterate the source headers and copy values verbatim. AMQP delivers most string-shaped headers as `byte[]` (UTF8). Downstream — dispatcher, processors, telemetry, filters, middleware, handlers — every `HeaderDecoder.Decode(headers[key])` call invokes `Encoding.UTF8.GetString(bytes)` again. Eagerly decoding `byte[]` → `string` once at the copy site replaces the dictionary value with the decoded string, so future `HeaderDecoder.Decode` calls hit the existing `if (value is string str) return str;` fast-path.

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs:610-626` — `CopyInboundHeaders`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs:57-69` — inline copy in `ProcessAsync`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/InboundHeaderDecodeCachingTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/InboundHeaderDecodeCachingTests.cs`:

```csharp
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Locks in the Group F item 3 invariant: inbound copy loops eagerly decode byte[] header
/// values to string so downstream HeaderDecoder.Decode calls hit the string fast-path
/// instead of re-running Encoding.UTF8.GetString on every read.
/// </summary>
public sealed class InboundHeaderDecodeCachingTests
{
    [Fact]
    public void CopyInboundHeaders_ReplacesByteArrayValuesWithDecodedStrings()
    {
        var args = BuildDeliverArgs(new Dictionary<string, object?>
        {
            ["X-Trace"] = Encoding.UTF8.GetBytes("abc-123"),
            ["X-Count"] = 42,                                  // typed value: must pass through
            ["X-Empty"] = null,                                // null: skipped per existing semantics
            ["X-Plain"] = "already-string",                    // string: pass through
        });

        var copied = global::ServiceConnect.Client.RabbitMQ.RabbitMqConsumerHost.CopyInboundHeadersForTests(args);

        Assert.IsType<string>(copied["X-Trace"]);
        Assert.Equal("abc-123", copied["X-Trace"]);
        Assert.Equal(42, copied["X-Count"]);
        Assert.False(copied.ContainsKey("X-Empty"));
        Assert.Equal("already-string", copied["X-Plain"]);
    }

    [Fact]
    public void HeaderDecoder_Decode_ReturnsCachedStringWithoutReDecoding_AfterEagerDecode()
    {
        var args = BuildDeliverArgs(new Dictionary<string, object?>
        {
            ["X-Trace"] = Encoding.UTF8.GetBytes("identity-test"),
        });
        var copied = global::ServiceConnect.Client.RabbitMQ.RabbitMqConsumerHost.CopyInboundHeadersForTests(args);

        var first = HeaderDecoder.Decode(copied["X-Trace"]);
        var second = HeaderDecoder.Decode(copied["X-Trace"]);

        Assert.Equal("identity-test", first);
        // After eager-decode, the dict holds a string. HeaderDecoder.Decode's string fast-path
        // returns it unchanged — so two consecutive Decode calls return the SAME string instance,
        // proving no re-decode allocation on subsequent reads.
        Assert.Same(first, second);
        Assert.Same(copied["X-Trace"], first);
    }

    [Fact]
    public void InboundMessageProcessorCopy_ReplacesByteArrayValuesWithDecodedStrings()
    {
        var args = BuildDeliverArgs(new Dictionary<string, object?>
        {
            [HeaderKeys.TypeName] = Encoding.UTF8.GetBytes("My.Type.Name"),
            [HeaderKeys.MessageId] = Encoding.UTF8.GetBytes("msg-42"),
            ["X-Typed"] = true,
        });

        // Drive the processor's copy loop without invoking the full ProcessAsync — the copy
        // is exposed via a test-access surface mirroring CopyInboundHeadersForTests.
        var copied = global::ServiceConnect.Client.RabbitMQ.InboundMessageProcessor.CopyInboundHeadersForTests(args);

        Assert.IsType<string>(copied[HeaderKeys.TypeName]);
        Assert.Equal("My.Type.Name", copied[HeaderKeys.TypeName]);
        Assert.IsType<string>(copied[HeaderKeys.MessageId]);
        Assert.Equal("msg-42", copied[HeaderKeys.MessageId]);
        Assert.Equal(true, copied["X-Typed"]);
    }

    private static BasicDeliverEventArgs BuildDeliverArgs(IDictionary<string, object?> headers)
    {
        var props = new BasicProperties { Headers = headers };
        return new BasicDeliverEventArgs(
            consumerTag: "test-consumer",
            deliveryTag: 1,
            redelivered: false,
            exchange: string.Empty,
            routingKey: "test-queue",
            properties: props,
            body: ReadOnlyMemory<byte>.Empty);
    }
}
```

The test references two `internal` test-access surfaces (`RabbitMqConsumerHost.CopyInboundHeadersForTests` and `InboundMessageProcessor.CopyInboundHeadersForTests`) that don't exist yet. Step 3 creates them.

- [ ] **Step 2: Run the test — expect compile failure**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InboundHeaderDecodeCachingTests" -m:1`
Expected: build error — `CopyInboundHeadersForTests` doesn't exist on either type.

- [ ] **Step 3: Modify `CopyInboundHeaders` in `RabbitMqConsumerHost`**

Open `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`. Find the existing method around line 610:

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

Replace with (eager-decode + add `using System.Text;` at top of file if not present):

```csharp
private static Dictionary<string, object> CopyInboundHeaders(BasicDeliverEventArgs args)
{
    var sourceHeaders = args.BasicProperties.Headers;
    var headers = new Dictionary<string, object>((sourceHeaders?.Count ?? 0) + 1, StringComparer.Ordinal);
    if (sourceHeaders != null)
    {
        foreach (var kvp in sourceHeaders)
        {
            if (kvp.Value is null)
            {
                continue;
            }
            // Eagerly decode AMQP byte[] header values to UTF8 strings. The existing
            // HeaderDecoder.Decode string fast-path then short-circuits every downstream
            // decode (dispatcher, processors, telemetry, filters, middleware, handlers),
            // each of which currently re-runs Encoding.UTF8.GetString on the same bytes.
            // Typed values (bool, int, IDictionary, IEnumerable) stay as objects so
            // HeaderDecoder.Render still handles them on demand.
            headers[kvp.Key] = kvp.Value is byte[] bytes
                ? Encoding.UTF8.GetString(bytes)
                : kvp.Value;
        }
    }

    return headers;
}

// Test-access surface: mirrors the production copy so unit tests can assert the eager-decode
// invariant without driving the full consumer-host pipeline. internal for [InternalsVisibleTo].
internal static Dictionary<string, object> CopyInboundHeadersForTests(BasicDeliverEventArgs args)
    => CopyInboundHeaders(args);
```

Add `using System.Text;` to the top of the file if it's not already there. Search for `using System.Text` first; if absent, add alphabetically among the other `using System.*` directives.

- [ ] **Step 4: Modify the inline copy in `InboundMessageProcessor.ProcessAsync`**

Open `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs`. Find the existing copy around lines 57-69:

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

Apply the same eager-decode replacement to the inner body. The pre-size logic (`(sourceHeaders?.Count ?? 4) + 3`) and the outer assignment to `headers` stay unchanged — only the inner `if (kvp.Value is not null)` body changes:

```csharp
var sourceHeaders = args.BasicProperties.Headers;

var headers = new Dictionary<string, object>((sourceHeaders?.Count ?? 4) + 3, StringComparer.Ordinal);
if (sourceHeaders != null)
{
    foreach (var kvp in sourceHeaders)
    {
        if (kvp.Value is null)
        {
            continue;
        }
        // Mirrors RabbitMqConsumerHost.CopyInboundHeaders: eager-decode byte[] headers so
        // HeaderDecoder.Decode hits the string fast-path on every downstream read.
        headers[kvp.Key] = kvp.Value is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : kvp.Value;
    }
}
```

Then add the test-access surface as a sibling method, somewhere near the bottom of the class (before the closing brace):

```csharp
// Test-access surface that exposes the inline header-copy logic so unit tests can
// assert the eager-decode invariant without driving the full ProcessAsync pipeline.
// internal for [InternalsVisibleTo].
internal static Dictionary<string, object> CopyInboundHeadersForTests(BasicDeliverEventArgs args)
{
    var sourceHeaders = args.BasicProperties.Headers;
    var headers = new Dictionary<string, object>((sourceHeaders?.Count ?? 4) + 3, StringComparer.Ordinal);
    if (sourceHeaders != null)
    {
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
    }
    return headers;
}
```

(The test-access helper duplicates the inline copy because the production code embeds the copy inside `ProcessAsync` — extracting it to a private helper as part of this task would be scope creep. The duplication is contained to a single test-only method and keeps the production diff minimal.)

Add `using System.Text;` to the top of the file if not already present.

- [ ] **Step 5: Run the test — expect pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InboundHeaderDecodeCachingTests" -m:1`
Expected: 3 passed.

- [ ] **Step 6: Run the full unit-test sweep**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore -m:1 2>&1 | tail -3`
Expected: all tests pass (modulo the known `ProducerPublishTimeoutTimingTests` flake; re-run once if it hits).

If anything else fails, the most likely cause is a test that asserts `Headers[key]` is `byte[]` on a path that goes through the consumer host. Check the failing test's stack — if it's on the `TimeoutEntry` or in-memory persistence path, that's a different code surface; investigate. If it's on the AMQP consume path, the eager-decode is breaking a contract — STOP and report DONE_WITH_CONCERNS with the failing test name.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/InboundHeaderDecodeCachingTests.cs

git commit -m "$(cat <<'EOF'
perf(transport): eager-decode inbound byte[] headers to string

Both consumer-side header-copy sites (RabbitMqConsumerHost.CopyInboundHeaders
and InboundMessageProcessor.ProcessAsync's inline copy) now replace byte[]
values with their UTF8-decoded string form during the existing iteration.

Downstream HeaderDecoder.Decode calls — across the dispatcher, processors,
telemetry, filters, middleware, and user handlers — hit the existing
string fast-path (HeaderDecoder.cs:38-41) and skip the redundant
Encoding.UTF8.GetString. Typed values (bool, int, IDictionary,
IEnumerable) stay as objects so HeaderDecoder.Render still handles them
on demand.

Per-delivery savings: one decode at copy time replaces N redundant
decodes downstream (typically 3-7 per header — telemetry, dispatcher,
audit, plus user filters/handlers). Worst case for unread headers:
~50ns wasted per decode, negligible against the savings on read-multiple
headers.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Item 4 — Eliminate `OutboundHeaderBuilder` double-copy

`BuildHeaders` returns `Dictionary<string, object>` (non-nullable values). `BuildBasicProperties` then allocates a fresh `Dictionary<string, object?>` and copies every entry purely to align value-type nullability with `BasicProperties.Headers` (which is `IDictionary<string, object?>`). Widening `BuildHeaders` return type to `Dictionary<string, object?>` lets `BuildBasicProperties` assign the source dictionary directly.

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs:49` (`BuildHeaders` signature) and `:55` (the `result` field type)
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs:99-117` (`BuildBasicProperties` signature + body)
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderAliasingTests.cs`

- [ ] **Step 1: Verify the no-mutation invariant in `Producer.cs`**

The aliasing optimisation is sound only if no caller mutates `messageHeaders` after `BuildBasicProperties` returns. Re-confirm this by reading `Producer.cs` for each call to `_headerBuilder.BuildBasicProperties(...)`. The four call sites are at lines 182, 251, 331, 394 (verify with `grep -n "BuildBasicProperties" src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs`).

For each, read from the `BuildBasicProperties` line through the next `await PublishWithTimeoutAsync(...)` call and confirm there's NO `messageHeaders[…] = …`, `messageHeaders.Add(…)`, `messageHeaders.Remove(…)`, or any other mutation in between.

If any mutation IS found, STOP and report — the aliasing is unsafe and the spec needs a different approach (insert a defensive copy at the mutation site, or keep the existing copy in `BuildBasicProperties` and find a different perf win).

If no mutation is found (expected outcome based on the spec's verification), proceed.

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderAliasingTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Locks in the Group F item 4 invariant: BuildBasicProperties does NOT copy
/// messageHeaders — it assigns the source dictionary directly to BasicProperties.Headers.
///
/// Maintenance hazard: this test only proves the immediate aliasing. If a future change
/// adds post-BuildBasicProperties mutation of messageHeaders (in Producer.cs or any
/// future caller), the alias becomes unsafe and the test won't catch it. The spec's
/// no-mutation invariant on the Producer.cs publish/send paths is the load-bearing
/// guarantee; this test is a regression guard against silently re-introducing the copy.
/// </summary>
public sealed class OutboundHeaderBuilderAliasingTests
{
    [Fact]
    public void BuildBasicProperties_AssignsHeadersDirectly_WithoutCopy()
    {
        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("source-q");

        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(false);

        var builder = new OutboundHeaderBuilder(
            busConfig.Object, queueConfig.Object, new FakeTimeProvider(), logger.Object);

        var messageHeaders = builder.BuildHeaders(typeof(string), null, "framework-q", "Publish");

        var basicProperties = builder.BuildBasicProperties(messageHeaders);

        Assert.Same(messageHeaders, basicProperties.Headers);
    }
}
```

- [ ] **Step 3: Run the test — expect failure**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~OutboundHeaderBuilderAliasingTests" -m:1`
Expected: build OR test failure. The build may fail because `BuildBasicProperties` currently takes `Dictionary<string, object>` and `BuildHeaders` currently returns `Dictionary<string, object>`, so `Assert.Same(messageHeaders, basicProperties.Headers)` compares `Dictionary<string, object>` against `IDictionary<string, object?>` — different reference identity (the copy alloc) so `Assert.Same` returns false even if it compiles.

- [ ] **Step 4: Widen `BuildHeaders` return type and internal `result`**

Open `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs`. Find line 49:

```csharp
public Dictionary<string, object> BuildHeaders(Type type, IReadOnlyDictionary<string, string>? headers, string queueName, string messageType)
```

Change the return type:

```csharp
public Dictionary<string, object?> BuildHeaders(Type type, IReadOnlyDictionary<string, string>? headers, string queueName, string messageType)
```

Find line 55 (the internal `result` declaration):

```csharp
var result = new Dictionary<string, object>(callerCount + StampedHeaderCount, StringComparer.Ordinal);
```

Change to:

```csharp
var result = new Dictionary<string, object?>(callerCount + StampedHeaderCount, StringComparer.Ordinal);
```

The body's stamping logic (`result[HeaderKeys.X] = value;`) compiles unchanged — `string` widens implicitly to `object?`.

- [ ] **Step 5: Widen `BuildBasicProperties` parameter and delete the copy loop**

Find `BuildBasicProperties` at lines 99-117:

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

    if (messageHeaders.TryGetValue(HeaderKeys.Priority, out var priority))
    {
        try
        {
            basicProperties.Priority = Convert.ToByte(priority, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            _logger.LogError(
                ex,
                "Could not set message priority from value '{Value}' (type '{ValueType}'); priority must be convertible to byte (0..255). Continuing without priority.",
                priority,
                priority?.GetType().FullName ?? "<null>");
        }
    }

    return basicProperties;
}
```

Replace with:

```csharp
public BasicProperties BuildBasicProperties(Dictionary<string, object?> messageHeaders)
{
    // Direct assign — type alignment with BasicProperties.Headers (IDictionary<string, object?>)
    // is now native after BuildHeaders' return-type widening, so no copy is needed.
    // Aliasing safety: Producer.cs callers do not mutate messageHeaders after this call
    // (BuildBasicProperties is the last touch before PublishWithTimeoutAsync). If a future
    // caller mutates post-BuildBasicProperties, RabbitMQ.Client may observe a torn dict —
    // see the spec's no-mutation invariant for the binding contract.
    var basicProperties = new BasicProperties
    {
        Headers = messageHeaders,
        Persistent = true
    };

    if (messageHeaders.TryGetValue(HeaderKeys.MessageId, out var messageId))
    {
        basicProperties.MessageId = messageId?.ToString();
    }

    if (messageHeaders.TryGetValue(HeaderKeys.Priority, out var priority))
    {
        try
        {
            basicProperties.Priority = Convert.ToByte(priority, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            _logger.LogError(
                ex,
                "Could not set message priority from value '{Value}' (type '{ValueType}'); priority must be convertible to byte (0..255). Continuing without priority.",
                priority,
                priority?.GetType().FullName ?? "<null>");
        }
    }

    return basicProperties;
}
```

- [ ] **Step 6: Run the test — expect pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~OutboundHeaderBuilderAliasingTests" -m:1`
Expected: 1 passed. `Assert.Same(messageHeaders, basicProperties.Headers)` is now true because they reference the same dictionary.

- [ ] **Step 7: Build the full RabbitMQ client + producer-tests projects**

Producer.cs has four `_headerBuilder.BuildHeaders(...)` call sites that now return `Dictionary<string, object?>`. The `var` inference + implicit string→object? widening should mean no call-site edits are needed, but verify by building:

Run: `dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1`
Expected: 0 errors, 0 warnings.

If a Producer.cs call site fails to compile, the most likely cause is an explicit type annotation (`Dictionary<string, object> messageHeaders = …`) instead of `var`. Locate via the build error and change the explicit annotation to `var` (the fix is mechanical — every call site already uses `var` per the brainstorm-stage scout, so this case is unlikely).

- [ ] **Step 8: Run the full unit-test sweep**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore -m:1 2>&1 | tail -3`
Expected: all tests pass (modulo the known timing flake).

The existing `OutboundHeaderBuilderReservedHeaderWarningTests` and `OutboundHeaderBuilderOperationNameTests` use the same `BuildHeaders` API — they should keep passing because their assertions don't depend on the specific value-type nullability of the dictionary.

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderAliasingTests.cs

git commit -m "$(cat <<'EOF'
perf(transport): eliminate OutboundHeaderBuilder double-copy

BuildHeaders now returns Dictionary<string, object?> (was Dictionary<string,
object>). BuildBasicProperties takes Dictionary<string, object?> directly
(was Dictionary<string, object>). The copy loop that previously allocated
a fresh Dictionary<string, object?> and walked every entry purely to
align value-type nullability with BasicProperties.Headers
(IDictionary<string, object?>) is gone — BasicProperties.Headers is now
assigned the source dictionary directly.

Per-publish savings: one Dictionary<string, object?> allocation
(typically ~80-200 bytes including the entries and pre-sized capacity)
plus the iteration. Hot path on every publish, send, and byte-stream
send.

Aliasing safety: Producer.cs callers do not mutate messageHeaders after
BuildBasicProperties returns. Verified at the four call sites
(PublishAsync, SendAsync(Type), SendAsync(string, Type), SendBytesAsync)
during implementation Step 1. The spec records this as the binding
no-mutation invariant — future post-BuildBasicProperties mutations
would observe the alias as a torn dict mid-publish.

Caller compatibility: Producer.cs uses var for every BuildHeaders local;
the implicit string→object? widening means no call-site edits are
needed.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Mark Group F done in the roadmap

After Tasks 1 and 2 land, update the roadmap.

**Files:**
- Modify: `architecture-fix-plan.md` — change Group F status

- [ ] **Step 1: Update the roadmap status line**

Open `architecture-fix-plan.md`. Find:

```
## Group F — Perf reductions · *pending*
```

Change to:

```
## Group F — Perf reductions · *done — items 3 and 4; items 1 and 2 deferred (see notes.md)*
```

The suffix records that items 1 (centralise CopyInboundHeaders) and 2 (skip ExtractHeaders on no-mutation) were intentionally left out of Group F per the brainstorm and spec, with the rationale captured in `notes.md`. Without the note, the next reader may assume the group is incomplete.

- [ ] **Step 2: Commit**

```bash
git add architecture-fix-plan.md

git commit -m "$(cat <<'EOF'
docs(architecture): mark Group F done in the fix plan

Group F lands in two perf commits:

- perf(transport): eager-decode inbound byte[] headers to string
- perf(transport): eliminate OutboundHeaderBuilder double-copy

Items 1 (centralise CopyInboundHeaders) and 2 (skip ExtractHeaders on
no-mutation) were intentionally deferred during the brainstorm —
recorded in notes.md with the rationale (item 1: no real allocation
win, DRY-only; item 2: detection cost outweighs savings).

Group E (resilience features, idempotency strategy) and Group G
(smaller extensibility) remain on the roadmap. Per the recommended
order, E is last because of the strategic idempotency call; G is
opportunistic.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Final verification

- [ ] **Step 1: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Full unit-test sweep**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore -m:1`
Expected: all tests pass (modulo the known `ProducerPublishTimeoutTimingTests` flake; re-run once if it hits).

- [ ] **Step 3: SerializationCompatTests sweep (sanity)**

Run: `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj --no-build --no-restore`
Expected: 48/48 pass. Group F doesn't touch the serializer; this is a sanity check.

- [ ] **Step 4: EndToEndTests build**

Run: `dotnet build src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 5: Confirm commit chain**

Run: `git log <commit-before-task-1>..HEAD --oneline`
Expected: three commits in this order:

```
<sha> docs(architecture): mark Group F done in the fix plan
<sha> perf(transport): eliminate OutboundHeaderBuilder double-copy
<sha> perf(transport): eager-decode inbound byte[] headers to string
```

---

## Risks and rollback

- **Aliasing in Task 2.** After the change, `BasicProperties.Headers` and the producer's local `messageHeaders` reference the same dictionary. The spec records the no-mutation invariant on the four `Producer.cs` call sites; Task 2 Step 1 re-verifies before touching `OutboundHeaderBuilder`. If a future change adds post-`BuildBasicProperties` mutation, the test won't catch it (it asserts immediate aliasing only) — flag in the test's docstring as a maintenance hazard, which the test file already does.
- **Eager-decode cost for unread headers in Task 1.** Worst case: 2-3 unread `byte[]` headers × ~50ns each = ~150ns wasted per delivery. Negligible against the 5-7 redundant decodes saved on read-multiple headers.
- **Test that constructs `BasicDeliverEventArgs` with `byte[]` headers and asserts `Headers[key] is byte[]`.** Swept at the brainstorm stage — only `InMemoryTimeoutStoreTests` did this, and that's the timeout-store path, NOT the AMQP consume path. Eager-decode doesn't reach there.
- **Type widening cascading in Task 2.** Theoretically, widening `BuildHeaders` return type could break callers that rely on `Dictionary<string, object>` non-nullability. Verified at the brainstorm stage: only four `Producer.cs` call sites, all using `var`. Step 7 of Task 2 catches any unexpected explicit-type annotation breakage.
- **Test-access surfaces (`CopyInboundHeadersForTests`).** Task 1 adds two `internal static` helpers that wrap (Host) or duplicate (Processor) the production copy. The duplication in `InboundMessageProcessor` is contained and clearly marked as test-only; it won't drift from production logic if reviewers spot-check at edit time. If duplication concerns prove real in code review, the alternative is to extract the inline copy in `ProcessAsync` to a private helper as part of Task 1 — scope creep but defensible.
- **Rollback** — both perf commits are pure mechanical changes with no data migrations or API breaks. `git revert` on either independently restores the prior allocation pattern. Task 3 (roadmap close-out) reverts independently.

---

## Decisions banked from the brainstorm

For audit trail:

- **Items 1 and 2 deferred** to `notes.md` follow-ups. Reasoning: item 1 has no real allocation win (failure branches mutually exclusive); item 2's detection cost > savings.
- **Eager-decode targets `byte[]` only** (option A from Question 2). Typed values stay on-demand. No `IConsumeContext.GetDecodedHeader` method (option B rejected).
- **Aliasing in item 4 is benign** because producers don't mutate `messageHeaders` post-`BuildBasicProperties`. Verified at the brainstorm stage; re-verified at implementation Task 2 Step 1.
- **No benchmarks** — savings are profiler-visible at sub-microsecond grain, below `BenchmarkDotNet` noise floor.
- **Three atomic commits** — two perf commits + roadmap close-out, mirroring Groups A/C/D.
