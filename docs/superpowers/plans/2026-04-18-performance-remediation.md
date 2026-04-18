# Performance Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve all 10 findings from `docs/performance-review-report-2026-04-15.md` per the design at `docs/superpowers/specs/2026-04-18-performance-remediation-design.md`.

**Architecture:** Eliminate redundant byte copies on the hot deserialize and stream-assembly paths by introducing `ReadOnlyMemoryStream` and `ReadOnlySequenceStream` wrappers and adding `ReadOnlyMemory<byte>` / `ReadOnlySequence<byte>` overloads to `IMessageSerializer`. Separately apply mechanical fixes to the medium- and low-severity findings: drop the `Task.Run` branch in the stream processor, drop the `ReadOnlyDictionary` wrapper in `ConsumeContextPool`, add a sorted index for in-memory timeouts, collapse MongoDB timeout polling from 3 → 2 round trips via `$facet`, add a shared E2E polling helper, and perform small cleanups in RabbitMQ header stamping, telemetry context detection, and bus startup.

**Tech Stack:** .NET 8+, xUnit, Moq, Newtonsoft.Json, MongoDB.Driver, RabbitMQ.Client, Testcontainers.

---

## File Structure

**New files:**
- `src/ServiceConnect/Services/IO/ReadOnlyMemoryStream.cs` — read-only `Stream` wrapper over `ReadOnlyMemory<byte>`; zero-copy source for `StreamReader`/`JsonTextReader`.
- `src/ServiceConnect/Services/IO/ReadOnlySequenceStream.cs` — read-only `Stream` wrapper over `ReadOnlySequence<byte>`; walks segments on `Read`.
- `src/ServiceConnect.UnitTests/Services/IO/ReadOnlyMemoryStreamTests.cs` — unit tests.
- `src/ServiceConnect.UnitTests/Services/IO/ReadOnlySequenceStreamTests.cs` — unit tests.
- `src/ServiceConnect.EndToEndTests/Helpers/TestPolling.cs` — polling helper for E2E tests.

**Modified files:**
- `src/ServiceConnect.Interfaces/IMessageSerializer.cs` — add `ReadOnlyMemory<byte>` and `ReadOnlySequence<byte>` overloads.
- `src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs` — implement new overloads, route existing overloads through memory path.
- `src/ServiceConnect.Interfaces/IMessageBusReadStream.cs` — add `ReadSequence()`.
- `src/ServiceConnect/Services/MessageBusReadStream.cs` — implement `ReadSequence()`, keep `Read()` as sequence-to-array shim.
- `src/ServiceConnect/Services/MessageDispatcher.cs` — use memory overload.
- `src/ServiceConnect/Services/RequestReplyManager.cs` — use memory overload.
- `src/ServiceConnect/Services/Processors/StreamProcessor.cs` — use `ReadSequence`, remove `Task.Run` branch.
- `src/ServiceConnect/Services/ConsumeContextPool.cs` — drop `ReadOnlyDictionary` wrapper.
- `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs` — add sorted timeout index.
- `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs` — use sorted index.
- `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs` — use `$facet` aggregation.
- `src/ServiceConnect.EndToEndTests/RetryAndErrorQueueTests.cs`, `CustomErrorQueueTests.cs`, `AggregatorExceptionTests.cs`, `MalformedMessageTests.cs`, `ProcessManagerExceptionTests.cs`, `QueuePurgeTests.cs`, `AuditingTests.cs` — use polling helper.
- `src/ServiceConnect.Client.RabbitMQ/Producer.cs` — `TryAdd` in `GetHeaders`.
- `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs` — remove redundant pre-scan in `TryGetExistingContext`.
- `src/ServiceConnect/Bus.cs` — single-pass `HashSet` for type names.

---

## Tier 1 — High Severity (items 1–2)

### Task 1: `ReadOnlyMemoryStream`

**Files:**
- Create: `src/ServiceConnect/Services/IO/ReadOnlyMemoryStream.cs`
- Create: `src/ServiceConnect.UnitTests/Services/IO/ReadOnlyMemoryStreamTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/ServiceConnect.UnitTests/Services/IO/ReadOnlyMemoryStreamTests.cs`:

```csharp
using System.Text;
using ServiceConnect.Services.IO;
using Xunit;

namespace ServiceConnect.UnitTests.Services.IO;

public class ReadOnlyMemoryStreamTests
{
    [Fact]
    public void Read_ReturnsAllBytes_InOrder()
    {
        var source = Encoding.UTF8.GetBytes("hello world");
        using var stream = new ReadOnlyMemoryStream(source);

        var buffer = new byte[source.Length];
        int read = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(source.Length, read);
        Assert.Equal(source, buffer);
    }

    [Fact]
    public void Read_AcrossMultipleCalls_ConcatenatesToFullPayload()
    {
        var source = Encoding.UTF8.GetBytes("abcdefghij");
        using var stream = new ReadOnlyMemoryStream(source);

        var buffer = new byte[4];
        Assert.Equal(4, stream.Read(buffer, 0, 4));
        Assert.Equal(new byte[] { (byte)'a', (byte)'b', (byte)'c', (byte)'d' }, buffer);

        Assert.Equal(4, stream.Read(buffer, 0, 4));
        Assert.Equal(new byte[] { (byte)'e', (byte)'f', (byte)'g', (byte)'h' }, buffer);

        var tail = new byte[4];
        Assert.Equal(2, stream.Read(tail, 0, 4));
        Assert.Equal((byte)'i', tail[0]);
        Assert.Equal((byte)'j', tail[1]);
    }

    [Fact]
    public void Read_AfterEnd_ReturnsZero()
    {
        using var stream = new ReadOnlyMemoryStream(new byte[] { 1, 2 });
        var buffer = new byte[4];
        Assert.Equal(2, stream.Read(buffer, 0, 4));
        Assert.Equal(0, stream.Read(buffer, 0, 4));
    }

    [Fact]
    public void CanSeek_IsFalse()
    {
        using var stream = new ReadOnlyMemoryStream(new byte[0]);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void Length_MatchesInputLength()
    {
        using var stream = new ReadOnlyMemoryStream(new byte[17]);
        Assert.Equal(17, stream.Length);
    }
}
```

- [ ] **Step 2: Run tests, verify they fail with compile error**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~ReadOnlyMemoryStreamTests"`
Expected: build failure — `ReadOnlyMemoryStream` does not exist.

- [ ] **Step 3: Implement `ReadOnlyMemoryStream`**

Create `src/ServiceConnect/Services/IO/ReadOnlyMemoryStream.cs`:

```csharp
namespace ServiceConnect.Services.IO;

internal sealed class ReadOnlyMemoryStream : Stream
{
    private readonly ReadOnlyMemory<byte> _buffer;
    private int _position;

    public ReadOnlyMemoryStream(ReadOnlyMemory<byte> buffer)
    {
        _buffer = buffer;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _buffer.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        int remaining = _buffer.Length - _position;
        if (remaining <= 0) return 0;
        int toCopy = Math.Min(remaining, count);
        _buffer.Span.Slice(_position, toCopy).CopyTo(buffer.AsSpan(offset, toCopy));
        _position += toCopy;
        return toCopy;
    }

    public override int Read(Span<byte> buffer)
    {
        int remaining = _buffer.Length - _position;
        if (remaining <= 0) return 0;
        int toCopy = Math.Min(remaining, buffer.Length);
        _buffer.Span.Slice(_position, toCopy).CopyTo(buffer[..toCopy]);
        _position += toCopy;
        return toCopy;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
```

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~ReadOnlyMemoryStreamTests"`
Expected: all 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/IO/ReadOnlyMemoryStream.cs src/ServiceConnect.UnitTests/Services/IO/ReadOnlyMemoryStreamTests.cs
git commit -m "perf: add ReadOnlyMemoryStream for zero-copy deserialization"
```

---

### Task 2: `ReadOnlySequenceStream`

**Files:**
- Create: `src/ServiceConnect/Services/IO/ReadOnlySequenceStream.cs`
- Create: `src/ServiceConnect.UnitTests/Services/IO/ReadOnlySequenceStreamTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/ServiceConnect.UnitTests/Services/IO/ReadOnlySequenceStreamTests.cs`:

```csharp
using System.Buffers;
using System.Text;
using ServiceConnect.Services.IO;
using Xunit;

namespace ServiceConnect.UnitTests.Services.IO;

public class ReadOnlySequenceStreamTests
{
    [Fact]
    public void Read_SingleSegmentSequence_ReturnsAllBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("hello");
        var sequence = new ReadOnlySequence<byte>(bytes);
        using var stream = new ReadOnlySequenceStream(sequence);

        var buffer = new byte[bytes.Length];
        int read = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(bytes.Length, read);
        Assert.Equal(bytes, buffer);
    }

    [Fact]
    public void Read_MultiSegmentSequence_ReturnsSegmentsConcatenated()
    {
        var first = MemorySegment<byte>.Chain(
            Encoding.UTF8.GetBytes("abc"),
            Encoding.UTF8.GetBytes("def"),
            Encoding.UTF8.GetBytes("gh"));
        var sequence = first.Sequence;

        using var stream = new ReadOnlySequenceStream(sequence);
        var buffer = new byte[8];
        int total = 0;
        int read;
        while ((read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            total += read;

        Assert.Equal(8, total);
        Assert.Equal(Encoding.UTF8.GetBytes("abcdefgh"), buffer);
    }

    [Fact]
    public void Read_AfterEnd_ReturnsZero()
    {
        var bytes = new byte[] { 1, 2, 3 };
        using var stream = new ReadOnlySequenceStream(new ReadOnlySequence<byte>(bytes));
        var buffer = new byte[4];
        stream.Read(buffer, 0, 4);
        Assert.Equal(0, stream.Read(buffer, 0, 4));
    }

    [Fact]
    public void Length_MatchesSequenceLength()
    {
        var bytes = new byte[42];
        using var stream = new ReadOnlySequenceStream(new ReadOnlySequence<byte>(bytes));
        Assert.Equal(42, stream.Length);
    }

    private sealed class MemorySegment<T> : ReadOnlySequenceSegment<T>
    {
        public MemorySegment(ReadOnlyMemory<T> memory) { Memory = memory; }

        public static MemorySegment<T> Chain(params ReadOnlyMemory<T>[] segments)
        {
            var head = new MemorySegment<T>(segments[0]);
            var current = head;
            long runningIndex = segments[0].Length;
            for (int i = 1; i < segments.Length; i++)
            {
                var next = new MemorySegment<T>(segments[i]) { RunningIndex = runningIndex };
                current.Next = next;
                current = next;
                runningIndex += segments[i].Length;
            }
            head._tail = current;
            return head;
        }

        private MemorySegment<T>? _tail;
        public ReadOnlySequence<T> Sequence => new(this, 0, _tail!, _tail!.Memory.Length);
    }
}
```

- [ ] **Step 2: Run tests, verify they fail with compile error**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~ReadOnlySequenceStreamTests"`
Expected: build failure — `ReadOnlySequenceStream` does not exist.

- [ ] **Step 3: Implement `ReadOnlySequenceStream`**

Create `src/ServiceConnect/Services/IO/ReadOnlySequenceStream.cs`:

```csharp
using System.Buffers;

namespace ServiceConnect.Services.IO;

internal sealed class ReadOnlySequenceStream : Stream
{
    private ReadOnlySequence<byte> _remaining;
    private readonly long _length;

    public ReadOnlySequenceStream(ReadOnlySequence<byte> sequence)
    {
        _remaining = sequence;
        _length = sequence.Length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _length - _remaining.Length;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (_remaining.IsEmpty || buffer.IsEmpty) return 0;
        int toCopy = (int)Math.Min(_remaining.Length, buffer.Length);
        _remaining.Slice(0, toCopy).CopyTo(buffer);
        _remaining = _remaining.Slice(toCopy);
        return toCopy;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
```

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~ReadOnlySequenceStreamTests"`
Expected: all 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/IO/ReadOnlySequenceStream.cs src/ServiceConnect.UnitTests/Services/IO/ReadOnlySequenceStreamTests.cs
git commit -m "perf: add ReadOnlySequenceStream for zero-copy stream assembly"
```

---

### Task 3: Add `ReadOnlyMemory<byte>` and `ReadOnlySequence<byte>` overloads to `IMessageSerializer`

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IMessageSerializer.cs`
- Modify: `src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs`
- Modify: `src/ServiceConnect.UnitTests/NewtonsoftJsonMessageSerializerTests.cs`

- [ ] **Step 1: Write failing tests for the new overloads**

Append to `src/ServiceConnect.UnitTests/NewtonsoftJsonMessageSerializerTests.cs` inside the existing class:

```csharp
[Fact]
public void Deserialize_Memory_RoundTripsMessage()
{
    var original = new FakeMessage1(Guid.NewGuid()) { Username = "Dave" };
    var bytes = _serializer.Serialize(original);

    var result = _serializer.Deserialize(new ReadOnlyMemory<byte>(bytes), typeof(FakeMessage1));

    var typed = Assert.IsType<FakeMessage1>(result);
    Assert.Equal(original.Username, typed.Username);
}

[Fact]
public void Deserialize_MemoryGeneric_RoundTripsMessage()
{
    var original = new FakeMessage1(Guid.NewGuid()) { Username = "Eve" };
    var bytes = _serializer.Serialize(original);

    var result = _serializer.Deserialize<FakeMessage1>(new ReadOnlyMemory<byte>(bytes));

    Assert.Equal(original.Username, result.Username);
}

[Fact]
public void Deserialize_Sequence_SingleSegment_RoundTripsMessage()
{
    var original = new FakeMessage1(Guid.NewGuid()) { Username = "Frank" };
    var bytes = _serializer.Serialize(original);
    var sequence = new System.Buffers.ReadOnlySequence<byte>(bytes);

    var result = _serializer.Deserialize(in sequence, typeof(FakeMessage1));

    var typed = Assert.IsType<FakeMessage1>(result);
    Assert.Equal(original.Username, typed.Username);
}

[Fact]
public void Deserialize_Memory_ThrowsSerializationException_OnInvalidJson()
{
    var invalidBytes = Encoding.UTF8.GetBytes("{ not json !!");
    Assert.Throws<SerializationException>(() =>
        _serializer.Deserialize(new ReadOnlyMemory<byte>(invalidBytes), typeof(FakeMessage1)));
}
```

- [ ] **Step 2: Run tests, verify they fail**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~NewtonsoftJsonMessageSerializerTests"`
Expected: build failure — new overloads not defined.

- [ ] **Step 3: Add overloads to `IMessageSerializer`**

Replace the contents of `src/ServiceConnect.Interfaces/IMessageSerializer.cs` with:

```csharp
using System.Buffers;

namespace ServiceConnect.Interfaces;

public interface IMessageSerializer
{
    byte[] Serialize<T>(T message) where T : Message;
    void Serialize<T>(T message, IBufferWriter<byte> output) where T : Message;
    T Deserialize<T>(byte[] data) where T : Message;
    T Deserialize<T>(ReadOnlySpan<byte> data) where T : Message;
    T Deserialize<T>(ReadOnlyMemory<byte> data) where T : Message;
    object Deserialize(byte[] data, Type type);
    object Deserialize(ReadOnlySpan<byte> data, Type type);
    object Deserialize(ReadOnlyMemory<byte> data, Type type);
    object Deserialize(in ReadOnlySequence<byte> data, Type type);
}
```

- [ ] **Step 4: Implement overloads in `NewtonsoftJsonMessageSerializer`**

Replace the deserialize methods in `src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs` (lines 69–101) with:

```csharp
public T Deserialize<T>(byte[] data) where T : Message
    => (T)Deserialize(new ReadOnlyMemory<byte>(data), typeof(T));

public T Deserialize<T>(ReadOnlySpan<byte> data) where T : Message
    => (T)Deserialize(new ReadOnlyMemory<byte>(data.ToArray()), typeof(T));

public T Deserialize<T>(ReadOnlyMemory<byte> data) where T : Message
    => (T)Deserialize(data, typeof(T));

public object Deserialize(byte[] data, Type type)
    => Deserialize(new ReadOnlyMemory<byte>(data), type);

public object Deserialize(ReadOnlySpan<byte> data, Type type)
    => Deserialize(new ReadOnlyMemory<byte>(data.ToArray()), type);

public object Deserialize(ReadOnlyMemory<byte> data, Type type)
{
    try
    {
        using var ms = new IO.ReadOnlyMemoryStream(data);
        using var sr = new StreamReader(ms, Encoding.UTF8);
        using var jr = new JsonTextReader(sr);
        return _serializer.Deserialize(jr, type)
            ?? throw new Interfaces.Exceptions.SerializationException(
                $"Deserialization returned null for type {type.Name}", type);
    }
    catch (JsonException ex)
    {
        throw new Interfaces.Exceptions.SerializationException(
            $"Failed to deserialize message of type {type.Name}", type, ex);
    }
}

public object Deserialize(in System.Buffers.ReadOnlySequence<byte> data, Type type)
{
    try
    {
        using var ms = new IO.ReadOnlySequenceStream(data);
        using var sr = new StreamReader(ms, Encoding.UTF8);
        using var jr = new JsonTextReader(sr);
        return _serializer.Deserialize(jr, type)
            ?? throw new Interfaces.Exceptions.SerializationException(
                $"Deserialization returned null for type {type.Name}", type);
    }
    catch (JsonException ex)
    {
        throw new Interfaces.Exceptions.SerializationException(
            $"Failed to deserialize message of type {type.Name}", type, ex);
    }
}
```

- [ ] **Step 5: Run serializer tests, verify pass**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~NewtonsoftJsonMessageSerializerTests"`
Expected: all 9 tests (5 existing + 4 new) pass.

- [ ] **Step 6: Run full unit-test suite to catch interface breakage**

Run: `dotnet test src/ServiceConnect.UnitTests`
Expected: all tests pass. If any test mocks `IMessageSerializer`, it still compiles because new methods are added, not renamed.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Interfaces/IMessageSerializer.cs src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs src/ServiceConnect.UnitTests/NewtonsoftJsonMessageSerializerTests.cs
git commit -m "perf: add ReadOnlyMemory/ReadOnlySequence serializer overloads"
```

---

### Task 4: Switch deserialize call sites to the memory overload

**Files:**
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs:78-80`
- Modify: `src/ServiceConnect/Services/RequestReplyManager.cs:133-135`

- [ ] **Step 1: Update `MessageDispatcher`**

In `src/ServiceConnect/Services/MessageDispatcher.cs`, replace lines 78–80:

```csharp
            // 5. Deserialize the message — .ToArray() at the serializer boundary (P-003).
            //    When P-040 adds span-based overloads, this allocation goes away.
            var message = _serializer.Deserialize(messageBytes.ToArray(), type);
```

with:

```csharp
            var message = _serializer.Deserialize(messageBytes, type);
```

- [ ] **Step 2: Update `RequestReplyManager`**

In `src/ServiceConnect/Services/RequestReplyManager.cs`, replace line 135:

```csharp
        object reply = _serializer.Deserialize(messageBytes.ToArray(), state.ReplyType);
```

with:

```csharp
        object reply = _serializer.Deserialize(messageBytes, state.ReplyType);
```

- [ ] **Step 3: Run dispatcher and reply-manager tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~MessageDispatcherTests|FullyQualifiedName~RequestReplyManagerTests"`
Expected: all tests pass.

- [ ] **Step 4: Run full unit-test suite**

Run: `dotnet test src/ServiceConnect.UnitTests`
Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/MessageDispatcher.cs src/ServiceConnect/Services/RequestReplyManager.cs
git commit -m "perf: use ReadOnlyMemory overload on deserialize call sites"
```

---

### Task 5: `MessageBusReadStream.ReadSequence()`

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IMessageBusReadStream.cs`
- Modify: `src/ServiceConnect/Services/MessageBusReadStream.cs`
- Modify: `src/ServiceConnect.UnitTests/MessageBusReadStreamTests.cs`

- [ ] **Step 1: Write a failing test for `ReadSequence`**

Append to `src/ServiceConnect.UnitTests/MessageBusReadStreamTests.cs` inside the existing class:

```csharp
[Fact]
public void ReadSequence_ReturnsPacketsInOrder_WithoutCopy()
{
    var stream = new MessageBusReadStream("seq");
    stream.Write(new byte[] { 1, 2 }, 0);
    stream.Write(new byte[] { 3, 4, 5 }, 1);
    stream.Write(new byte[] { 6 }, 2);
    stream.SetLastPacketNumber(2);

    var sequence = stream.ReadSequence();

    Assert.Equal(6, sequence.Length);
    Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, sequence.ToArray());
}

[Fact]
public void ReadSequence_ThrowsIfIncomplete()
{
    var stream = new MessageBusReadStream("seq");
    stream.Write(new byte[] { 1 }, 0);
    stream.SetLastPacketNumber(2);

    Assert.Throws<InvalidOperationException>(() => stream.ReadSequence());
}
```

- [ ] **Step 2: Run the tests, verify they fail**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~MessageBusReadStreamTests"`
Expected: build failure — `ReadSequence` does not exist.

- [ ] **Step 3: Add `ReadSequence()` to `IMessageBusReadStream`**

In `src/ServiceConnect.Interfaces/IMessageBusReadStream.cs`, add the method alongside the existing `Read`:

```csharp
using System.Buffers;

namespace ServiceConnect.Interfaces;

public interface IMessageBusReadStream
{
    string SequenceId { get; }
    long LastPacketNumber { get; }
    void SetLastPacketNumber(long lastPacketNumber);
    void Write(byte[] data, long packetNumber);
    byte[] Read();
    ReadOnlySequence<byte> ReadSequence();
    bool IsComplete();
}
```

If the existing interface has a different shape, preserve it and add only `ReadOnlySequence<byte> ReadSequence();`.

- [ ] **Step 4: Implement `ReadSequence()` in `MessageBusReadStream`**

In `src/ServiceConnect/Services/MessageBusReadStream.cs`, replace the existing `Read()` method (lines 49–63) and add `ReadSequence()`:

```csharp
public ReadOnlySequence<byte> ReadSequence()
{
    if (!IsComplete())
        throw new InvalidOperationException("Stream is not yet complete.");

    if (LastPacketNumber < 0)
        return ReadOnlySequence<byte>.Empty;

    PacketSegment? head = null;
    PacketSegment? tail = null;
    long runningIndex = 0;
    for (long i = 0; i <= LastPacketNumber; i++)
    {
        if (!_packets.TryGetValue(i, out var packet) || packet.Length == 0)
            continue;

        var segment = new PacketSegment(packet, runningIndex);
        if (head is null)
            head = segment;
        else
            tail!.SetNext(segment);
        tail = segment;
        runningIndex += packet.Length;
    }

    if (head is null)
        return ReadOnlySequence<byte>.Empty;

    return new ReadOnlySequence<byte>(head, 0, tail!, tail!.Memory.Length);
}

public byte[] Read()
{
    var sequence = ReadSequence();
    if (sequence.IsEmpty) return [];
    if (sequence.IsSingleSegment) return sequence.First.ToArray();
    return sequence.ToArray();
}

private sealed class PacketSegment : ReadOnlySequenceSegment<byte>
{
    public PacketSegment(ReadOnlyMemory<byte> memory, long runningIndex)
    {
        Memory = memory;
        RunningIndex = runningIndex;
    }

    public void SetNext(PacketSegment next) => Next = next;
}
```

Add `using System.Buffers;` at the top of the file.

- [ ] **Step 5: Run the stream tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~MessageBusReadStreamTests"`
Expected: all tests (existing + 2 new) pass.

- [ ] **Step 6: Run full unit-test suite**

Run: `dotnet test src/ServiceConnect.UnitTests`
Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Interfaces/IMessageBusReadStream.cs src/ServiceConnect/Services/MessageBusReadStream.cs src/ServiceConnect.UnitTests/MessageBusReadStreamTests.cs
git commit -m "perf: add MessageBusReadStream.ReadSequence for zero-copy assembly"
```

---

### Task 6: Switch `StreamProcessor` to `ReadSequence` and the sequence serializer overload

**Files:**
- Modify: `src/ServiceConnect/Services/Processors/StreamProcessor.cs:150-155`

- [ ] **Step 1: Update the call site**

In `src/ServiceConnect/Services/Processors/StreamProcessor.cs`, replace lines 152–153:

```csharp
            var assembledBytes = state.Stream.Read();
            var originalMessage = _serializer.Deserialize(assembledBytes, resolvedType);
```

with:

```csharp
            var assembled = state.Stream.ReadSequence();
            var originalMessage = _serializer.Deserialize(in assembled, resolvedType);
```

- [ ] **Step 2: Run stream processor tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~StreamProcessorTests"`
Expected: all tests pass.

- [ ] **Step 3: Run full unit-test suite**

Run: `dotnet test src/ServiceConnect.UnitTests`
Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect/Services/Processors/StreamProcessor.cs
git commit -m "perf: use ReadSequence on stream processor deserialize path"
```

---

## Tier 2 — Medium Severity (items 3–7)

### Task 7: Inline sync stream handler (remove `Task.Run`)

**Files:**
- Modify: `src/ServiceConnect/Services/Processors/StreamProcessor.cs:174-191`

- [ ] **Step 1: Update `InvokeHandlerAsync`**

In `src/ServiceConnect/Services/Processors/StreamProcessor.cs`, replace the entire body of `InvokeHandlerAsync` (lines 174–191) with:

```csharp
private Task<ProcessResult> InvokeHandlerAsync(
    StreamHandlerDescriptor descriptor,
    object handler,
    object originalMessage,
    CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    descriptor.InvokeExecute(handler, originalMessage);
    return HandledTask;
}
```

- [ ] **Step 2: Run stream processor tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~StreamProcessorTests"`
Expected: all tests pass. If any test asserted `Task.Run` behavior, update it to assert inline dispatch.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect/Services/Processors/StreamProcessor.cs
git commit -m "perf: inline sync stream handlers, remove Task.Run branch"
```

---

### Task 8: Drop `ReadOnlyDictionary` wrapper in `ConsumeContextPool`

**Files:**
- Modify: `src/ServiceConnect/Services/ConsumeContextPool.cs`
- Verify: `src/ServiceConnect.UnitTests/ConsumeContextTests.cs` still passes (if it covers pooled context)

- [ ] **Step 1: Write a failing test asserting no `ReadOnlyDictionary` wrapping**

Append to `src/ServiceConnect.UnitTests/ConsumeContextTests.cs` inside the existing class (or create a new test file `ConsumeContextPoolTests.cs` if no matching test class exists):

```csharp
[Fact]
public void Rent_DoesNotWrapHeadersInReadOnlyDictionary()
{
    var pool = new ConsumeContextPool();
    var headers = new Dictionary<string, object> { ["k"] = (object)"v" };
    var busConfig = new BusConfiguration();
    var queueConfig = new QueueConfiguration { QueueName = "q" };

    var ctx = pool.Rent(null!, headers, queueConfig, busConfig, CancellationToken.None);

    Assert.IsNotType<System.Collections.ObjectModel.ReadOnlyDictionary<string, object>>(ctx.Headers);
    Assert.Equal("v", ctx.Headers["k"]);
}
```

If `ConsumeContextPool` or `PooledConsumeContext` is `internal`, expose for tests via `InternalsVisibleTo("ServiceConnect.UnitTests")` in `src/ServiceConnect/ServiceConnect.csproj` (check whether this attribute already exists before adding).

- [ ] **Step 2: Run the test, verify it fails**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~Rent_DoesNotWrapHeadersInReadOnlyDictionary"`
Expected: test fails — `Headers` is a `ReadOnlyDictionary`.

- [ ] **Step 3: Update `ConsumeContextPool`**

In `src/ServiceConnect/Services/ConsumeContextPool.cs`:

Replace lines 11–12:

```csharp
    private static readonly IReadOnlyDictionary<string, object> EmptyHeaders =
        new ReadOnlyDictionary<string, object>(new Dictionary<string, object>());
```

with:

```csharp
    private static readonly Dictionary<string, object> EmptyHeaders = new();
```

Replace the `Initialize` body around lines 86–95:

```csharp
        _bus = bus;
        _queueConfig = queueConfig;
        _busConfig = busConfig;
        CancellationToken = cancellationToken;
        _headers = new ReadOnlyDictionary<string, object>(
            headers as Dictionary<string, object> ?? new Dictionary<string, object>(headers));
        _messageId = null;
        _messageIdCached = false;
        _correlationId = null;
```

with:

```csharp
        _bus = bus;
        _queueConfig = queueConfig;
        _busConfig = busConfig;
        CancellationToken = cancellationToken;
        _headers = headers as Dictionary<string, object>
                 ?? new Dictionary<string, object>(headers);
        _messageId = null;
        _messageIdCached = false;
        _correlationId = null;
```

Remove the now-unused `using System.Collections.ObjectModel;` at the top of the file.

- [ ] **Step 4: Run the test, verify it passes**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~ConsumeContextTests|FullyQualifiedName~ConsumeContextPoolTests"`
Expected: all tests pass.

- [ ] **Step 5: Run full unit-test suite**

Run: `dotnet test src/ServiceConnect.UnitTests`
Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect/Services/ConsumeContextPool.cs src/ServiceConnect.UnitTests/ConsumeContextTests.cs
git commit -m "perf: drop ReadOnlyDictionary wrapper in ConsumeContextPool"
```

---

### Task 9: Sorted timeout index for `InMemoryTimeoutStore`

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs`
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs`
- Modify: `src/ServiceConnect.UnitTests/` — add/extend timeout store tests (check existing test class name via `ls src/ServiceConnect.UnitTests/ | grep -i timeout`)

- [ ] **Step 1: Write a failing test verifying sorted polling**

Locate or create an `InMemoryTimeoutStoreTests.cs` file under `src/ServiceConnect.UnitTests/`. Append/insert:

```csharp
[Fact]
public async Task GetTimeoutsBatchAsync_OnlyReturnsDue_WithLargeFuturePopulation()
{
    var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
        new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero));
    var store = new InMemoryTimeoutStore(timeProvider: timeProvider);

    // Seed 1,000 future timeouts
    for (int i = 0; i < 1000; i++)
    {
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = Guid.NewGuid(),
            Time = timeProvider.GetUtcNow().AddMinutes(10 + i),
            Destination = "q",
            State = [],
            Headers = new Dictionary<string, object>()
        });
    }

    // Seed 3 due timeouts
    var dueIds = new List<Guid>();
    for (int i = 0; i < 3; i++)
    {
        var id = Guid.NewGuid();
        dueIds.Add(id);
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = id,
            Time = timeProvider.GetUtcNow().AddSeconds(-1),
            Destination = "q",
            State = [],
            Headers = new Dictionary<string, object>()
        });
    }

    var batch = await store.GetTimeoutsBatchAsync();

    Assert.Equal(3, batch.DueTimeouts.Count);
    Assert.All(batch.DueTimeouts, t => Assert.Contains(t.Id, dueIds));
    // NextQueryTime must be the earliest future timeout (now + 10 min).
    Assert.Equal(timeProvider.GetUtcNow().AddMinutes(10), batch.NextQueryTime);
}

[Fact]
public async Task RemoveDispatchedTimeoutAsync_RemovesFromIndex()
{
    var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
        new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero));
    var store = new InMemoryTimeoutStore(timeProvider: timeProvider);

    var id = Guid.NewGuid();
    await store.InsertTimeoutAsync(new TimeoutData
    {
        Id = id,
        Time = timeProvider.GetUtcNow().AddSeconds(-1),
        Destination = "q",
        State = [],
        Headers = new Dictionary<string, object>()
    });

    await store.RemoveDispatchedTimeoutAsync(id);

    var batch = await store.GetTimeoutsBatchAsync();
    Assert.Empty(batch.DueTimeouts);
}
```

- [ ] **Step 2: Run the tests, verify current behaviour is correct (tests should pass against the current scan-based impl)**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~InMemoryTimeoutStoreTests"`
Expected: tests pass (they lock in correctness; the refactor below must keep them passing).

- [ ] **Step 3: Add sorted index to `InMemoryPersistenceState`**

Replace `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs` with:

```csharp
namespace ServiceConnect.Persistence.InMemory;

internal sealed class InMemoryPersistenceState
{
    public InMemoryPersistenceState(TimeProvider? timeProvider = null)
    {
        Provider = new CacheProvider(timeProvider);
    }

    public CacheProvider Provider { get; }
    public ReaderWriterLockSlim SyncRoot { get; } = new();
    public SortedSet<TimeoutEntry> TimeoutIndex { get; } = new(TimeoutEntryComparer.Instance);

    internal readonly record struct TimeoutEntry(DateTimeOffset Time, Guid Id);

    private sealed class TimeoutEntryComparer : IComparer<TimeoutEntry>
    {
        public static readonly TimeoutEntryComparer Instance = new();

        public int Compare(TimeoutEntry x, TimeoutEntry y)
        {
            int cmp = x.Time.CompareTo(y.Time);
            return cmp != 0 ? cmp : x.Id.CompareTo(y.Id);
        }
    }
}
```

- [ ] **Step 4: Update `InMemoryTimeoutStore` to maintain and use the index**

Replace the `InsertTimeoutAsync`, `GetTimeoutsBatchAsync`, and `RemoveDispatchedTimeoutAsync` methods in `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs` with:

```csharp
public Task InsertTimeoutAsync(TimeoutData timeoutData, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();

    _state.SyncRoot.EnterWriteLock();
    try
    {
        string key = timeoutData.Id.ToString();

        if (_state.Provider.Contains(key))
            throw new PersistenceException($"TimeoutData with Id {key} already exists in the cache.");

        _state.Provider.Add(key, timeoutData, _timeProvider.GetUtcNow().Add(ExpiryDuration));
        _state.TimeoutIndex.Add(new(timeoutData.Time, timeoutData.Id));
    }
    finally
    {
        _state.SyncRoot.ExitWriteLock();
    }

    return Task.CompletedTask;
}

public Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();

    var retval = new TimeoutsBatch { DueTimeouts = [] };
    DateTimeOffset utcNow = _timeProvider.GetUtcNow();
    var nextQueryTime = DateTimeOffset.MaxValue;

    _state.SyncRoot.EnterWriteLock();
    try
    {
        // Walk the sorted head while entries are due.
        while (_state.TimeoutIndex.Min is { } head && head.Time <= utcNow)
        {
            _state.TimeoutIndex.Remove(head);
            var value = _state.Provider.Get<string, object>(head.Id.ToString());
            if (value is TimeoutData timeoutData)
                retval.DueTimeouts.Add(timeoutData);
            // If the provider no longer has the entry (absolute expiry fired), the removal
            // from the index above already prunes the stale reference.
        }

        // Peek the next future entry for the NextQueryTime hint.
        if (_state.TimeoutIndex.Min is { } next)
            nextQueryTime = next.Time;
    }
    finally
    {
        _state.SyncRoot.ExitWriteLock();
    }

    if (nextQueryTime == DateTimeOffset.MaxValue)
        nextQueryTime = utcNow.Add(DefaultNextQueryInterval);

    retval.NextQueryTime = nextQueryTime;
    return Task.FromResult(retval);
}

public Task RemoveDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();

    _state.SyncRoot.EnterWriteLock();
    try
    {
        string key = id.ToString();
        if (_state.Provider.Get<string, object>(key) is TimeoutData data)
            _state.TimeoutIndex.Remove(new(data.Time, id));
        _state.Provider.Remove(key);
    }
    finally
    {
        _state.SyncRoot.ExitWriteLock();
    }

    return Task.CompletedTask;
}
```

Note: `GetTimeoutsBatchAsync` now takes the **write** lock (not read), because it mutates the index when walking due entries.

- [ ] **Step 5: Run the tests, verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~InMemoryTimeoutStoreTests"`
Expected: all tests pass.

- [ ] **Step 6: Run full unit-test suite**

Run: `dotnet test src/ServiceConnect.UnitTests`
Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs src/ServiceConnect.UnitTests/InMemoryTimeoutStoreTests.cs
git commit -m "perf: sorted index for in-memory timeout polling"
```

---

### Task 10: MongoDB timeout polling — 3 → 2 round trips via `$facet`

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs:55-107`
- Verify: existing `MongoDbTimeoutStoreTests` still passes (currently a filter-shape unit test)
- E2E: existing MongoDB integration tests (if any) via `ServiceConnect.EndToEndTests`

- [ ] **Step 1: Define the typed facet DTO**

At the top of `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs` (inside the namespace, outside the class), add:

```csharp
internal sealed class TimeoutFacetResult
{
    public List<TimeoutData> Due { get; set; } = new();
    public List<NextTimeoutProjection> Next { get; set; } = new();
}

internal sealed class NextTimeoutProjection
{
    public Guid Id { get; set; }
    public DateTimeOffset Time { get; set; }
}
```

These DTOs let the driver handle BSON↔`DateTimeOffset` serialization with its default conventions, matching how `TimeoutData` is already stored.

- [ ] **Step 2: Update `GetTimeoutsBatchAsync` to use `$facet`**

Replace the body of `GetTimeoutsBatchAsync` in `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs` (lines 55–107) with:

```csharp
public async Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();

    try
    {
        var retval = new TimeoutsBatch { DueTimeouts = [] };
        var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
        var utcNow = _timeProvider.GetUtcNow();

        var sessionId = Guid.NewGuid();
        var dueUnlockedFilter = BuildDueTimeoutFilter(utcNow);
        var lockUpdate = Builders<TimeoutData>.Update
            .Set(x => x.Locked, true)
            .Set(x => x.LockedBy, sessionId)
            .Set(x => x.LockExpiresAt, utcNow.Add(LockLeaseDuration));
        await collection.UpdateManyAsync(dueUnlockedFilter, lockUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Single $facet aggregation: fetch our locked rows AND the next-future-timeout
        // projection in one round trip. When nothing was claimed, the "due" branch yields
        // an empty array and we still need the "next" branch to set NextQueryTime.
        var duePipeline = new EmptyPipelineDefinition<TimeoutData>()
            .Match(t => t.LockedBy == sessionId && t.Locked);

        var nextPipeline = new EmptyPipelineDefinition<TimeoutData>()
            .Match(t => t.Time > utcNow && !t.Locked)
            .Sort(Builders<TimeoutData>.Sort.Ascending(t => t.Time))
            .Limit(1)
            .Project(t => new NextTimeoutProjection { Id = t.Id, Time = t.Time });

        var facetPipeline = new EmptyPipelineDefinition<TimeoutData>()
            .Facet(
                AggregateFacet.Create("Due", duePipeline),
                AggregateFacet.Create("Next", nextPipeline));

        var facetResult = await collection.Aggregate(facetPipeline, cancellationToken: cancellationToken)
                                          .FirstOrDefaultAsync(cancellationToken)
                                          .ConfigureAwait(false);

        var nextQueryTime = DateTimeOffset.MaxValue;
        if (facetResult is not null)
        {
            var dueFacet = facetResult.Facets.FirstOrDefault(f => f.Name == "Due");
            if (dueFacet is AggregateFacetResults<TimeoutData> typedDue)
            {
                foreach (var doc in typedDue.Output)
                    retval.DueTimeouts.Add(doc);
            }

            var nextFacet = facetResult.Facets.FirstOrDefault(f => f.Name == "Next");
            if (nextFacet is AggregateFacetResults<NextTimeoutProjection> typedNext
                && typedNext.Output.Count > 0)
            {
                nextQueryTime = typedNext.Output[0].Time;
            }
        }

        if (nextQueryTime == DateTimeOffset.MaxValue)
            nextQueryTime = utcNow.Add(DefaultNextQueryInterval);

        retval.NextQueryTime = nextQueryTime;
        return retval;
    }
    catch (MongoException ex)
    {
        throw new PersistenceException("Failed to get timeouts batch.", ex);
    }
}
```

Add the following using if missing from the top of the file:

```csharp
using System.Linq;
```

- [ ] **Step 3: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~MongoDbTimeoutStoreTests"`
Expected: existing `BuildDueTimeoutFilter_IncludesExpiredLeasesForRecovery` still passes (unchanged filter shape).

- [ ] **Step 4: Run E2E MongoDB tests if present**

Run: `sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~Mongo|FullyQualifiedName~Timeout"'`
Expected: all MongoDB-backed timeout tests pass. If any test asserts the exact number of DB operations, update it.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs
git commit -m "perf: use \$facet aggregation for mongo timeout polling"
```

---

### Task 11: E2E polling helper + convert 7 call sites

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/Helpers/TestPolling.cs`
- Modify: `src/ServiceConnect.EndToEndTests/RetryAndErrorQueueTests.cs`
- Modify: `src/ServiceConnect.EndToEndTests/CustomErrorQueueTests.cs`
- Modify: `src/ServiceConnect.EndToEndTests/AggregatorExceptionTests.cs`
- Modify: `src/ServiceConnect.EndToEndTests/MalformedMessageTests.cs`
- Modify: `src/ServiceConnect.EndToEndTests/ProcessManagerExceptionTests.cs`
- Modify: `src/ServiceConnect.EndToEndTests/QueuePurgeTests.cs`
- Modify: `src/ServiceConnect.EndToEndTests/AuditingTests.cs`

- [ ] **Step 1: Create the polling helper**

Create `src/ServiceConnect.EndToEndTests/Helpers/TestPolling.cs`:

```csharp
namespace ServiceConnect.EndToEndTests.Helpers;

internal static class TestPolling
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);

    public static async Task<T?> WaitForAsync<T>(
        Func<Task<T?>> probe,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var interval = pollInterval ?? DefaultPollInterval;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await probe().ConfigureAwait(false);
            if (result is not null) return result;
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    public static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? DefaultPollInterval;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await condition().ConfigureAwait(false)) return true;
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }
}
```

- [ ] **Step 2: Convert `RetryAndErrorQueueTests.cs:98-103`**

In `src/ServiceConnect.EndToEndTests/RetryAndErrorQueueTests.cs`, replace:

```csharp
BasicGetResult? errorMsg = null;
for (int i = 0; i < 30 && errorMsg == null; i++)
{
    errorMsg = await channel.BasicGetAsync(errorQueueName, autoAck: true);
    if (errorMsg == null) await Task.Delay(1000);
}
```

with:

```csharp
var errorMsg = await TestPolling.WaitForAsync(
    async () => await channel.BasicGetAsync(errorQueueName, autoAck: true),
    timeout: TimeSpan.FromSeconds(30));
```

Add `using ServiceConnect.EndToEndTests.Helpers;` at the top of the file if not already present.

- [ ] **Step 3: Apply the same conversion to the other four error-queue polling tests**

Apply the identical conversion (replacing the `for (int i = 0; i < 30 && errorMsg == null; i++)` loops with `TestPolling.WaitForAsync`) to:
- `src/ServiceConnect.EndToEndTests/CustomErrorQueueTests.cs:~97`
- `src/ServiceConnect.EndToEndTests/AggregatorExceptionTests.cs:~96`
- `src/ServiceConnect.EndToEndTests/MalformedMessageTests.cs:~114`
- `src/ServiceConnect.EndToEndTests/ProcessManagerExceptionTests.cs:~103`

For each file:
- Replace the `for`/`BasicGetAsync`/`Task.Delay(1000)` loop with the `TestPolling.WaitForAsync` call as in Step 2.
- Add the `using ServiceConnect.EndToEndTests.Helpers;` import if missing.

- [ ] **Step 4: Convert the setup-delay sleeps using observable conditions**

`QueuePurgeTests.cs:103` and `AuditingTests.cs:85` are fixed sleeps that should become condition-based. Each test already has a verifiable post-condition — use those directly instead of waiting a fixed time.

**`QueuePurgeTests.cs:103` — the sleep follows `StartConsumingAsync` and precedes a `bus.SendAsync(...)` that expects a clean queue.** Replace:

```csharp
await bus.StartConsumingAsync();
await Task.Delay(1000); // wait for purge and consumer setup
```

with a direct purge + assertion via the RabbitMQ client, avoiding any wait:

```csharp
using (var factory = new ConnectionFactory { HostName = _fixture.RabbitMqHostname, Port = _fixture.RabbitMqPort, UserName = _fixture.RabbitMqUsername, Password = _fixture.RabbitMqPassword })
using (var conn = await factory.CreateConnectionAsync())
using (var channel = await conn.CreateChannelAsync())
{
    var queueName = /* derive from test context — see the SendAsync destination below */;
    var purgeResult = await TestPolling.WaitUntilAsync(
        async () =>
        {
            var get = await channel.BasicGetAsync(queueName, autoAck: true);
            return get is null; // queue is empty
        },
        TimeSpan.FromSeconds(5));
    Assert.True(purgeResult, "Queue did not become empty within timeout.");
}
await bus.StartConsumingAsync();
```

Before writing this, read the surrounding test body (lines ~80–120 of `QueuePurgeTests.cs`) to recover the actual queue name used for the test and the RabbitMQ fixture reference — they differ per test fixture. If the existing test already has a channel open from earlier in the method, reuse it instead of opening a new one.

**`AuditingTests.cs:85` — the sleep follows `SendAsync` and precedes an assertion that the audit queue contains the message.** Replace:

```csharp
await Task.Delay(1000); // allow audit publish to complete
```

with a poll on the audit queue for the expected message:

```csharp
var audited = await TestPolling.WaitForAsync(
    async () => await channel.BasicGetAsync(auditQueueName, autoAck: true),
    TimeSpan.FromSeconds(10));
Assert.NotNull(audited);
```

Again, use the actual `channel` / `auditQueueName` already in scope in the test.

- [ ] **Step 5: Run the converted E2E tests**

Run: `sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~RetryAndErrorQueue|FullyQualifiedName~CustomErrorQueue|FullyQualifiedName~AggregatorException|FullyQualifiedName~MalformedMessage|FullyQualifiedName~ProcessManagerException|FullyQualifiedName~QueuePurge|FullyQualifiedName~Auditing"'`
Expected: all tests pass and wall-clock runtime is materially shorter than before.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/Helpers/TestPolling.cs src/ServiceConnect.EndToEndTests/RetryAndErrorQueueTests.cs src/ServiceConnect.EndToEndTests/CustomErrorQueueTests.cs src/ServiceConnect.EndToEndTests/AggregatorExceptionTests.cs src/ServiceConnect.EndToEndTests/MalformedMessageTests.cs src/ServiceConnect.EndToEndTests/ProcessManagerExceptionTests.cs src/ServiceConnect.EndToEndTests/QueuePurgeTests.cs src/ServiceConnect.EndToEndTests/AuditingTests.cs
git commit -m "perf: replace fixed-delay polling with TestPolling helper in E2E tests"
```

---

## Tier 3 — Low Severity (items 8–10)

### Task 12: `Producer.GetHeaders` — `TryAdd` instead of `ContainsKey` + assign

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer.cs:283-300`

- [ ] **Step 1: Update the header stamping**

In `src/ServiceConnect.Client.RabbitMQ/Producer.cs`, replace lines 283–300:

```csharp
        if (!result.ContainsKey(HeaderKeys.DestinationAddress))
            result[HeaderKeys.DestinationAddress] = queueName;
        if (!result.ContainsKey(HeaderKeys.MessageId))
            result[HeaderKeys.MessageId] = Guid.NewGuid().ToString();
        if (!result.ContainsKey(HeaderKeys.MessageType))
            result[HeaderKeys.MessageType] = messageType;

        result[HeaderKeys.SourceAddress] = _queueConfiguration.QueueName;
        result[HeaderKeys.TimeSent] = FormatTimestamp(_timeProvider.GetUtcNow().UtcDateTime);
        if (_busConfiguration.IncludeMachineNameInHeaders)
            result[HeaderKeys.SourceMachine] = Environment.MachineName;

        // P-016: cache FullName and AssemblyQualifiedName per Type — these never change.
        var (fullName, aqn) = _typeNameCache.GetOrAdd(type, static t => (t.FullName!, t.AssemblyQualifiedName!));
        if (!result.ContainsKey(HeaderKeys.TypeName))
            result[HeaderKeys.TypeName] = fullName;
        if (!result.ContainsKey(HeaderKeys.FullTypeName))
            result[HeaderKeys.FullTypeName] = aqn;
```

with:

```csharp
        result.TryAdd(HeaderKeys.DestinationAddress, queueName);
        result.TryAdd(HeaderKeys.MessageId, Guid.NewGuid().ToString());
        result.TryAdd(HeaderKeys.MessageType, messageType);

        result[HeaderKeys.SourceAddress] = _queueConfiguration.QueueName;
        result[HeaderKeys.TimeSent] = FormatTimestamp(_timeProvider.GetUtcNow().UtcDateTime);
        if (_busConfiguration.IncludeMachineNameInHeaders)
            result[HeaderKeys.SourceMachine] = Environment.MachineName;

        // P-016: cache FullName and AssemblyQualifiedName per Type — these never change.
        var (fullName, aqn) = _typeNameCache.GetOrAdd(type, static t => (t.FullName!, t.AssemblyQualifiedName!));
        result.TryAdd(HeaderKeys.TypeName, fullName);
        result.TryAdd(HeaderKeys.FullTypeName, aqn);
```

- [ ] **Step 2: Run RabbitMQ client tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~Producer"`
Expected: all tests pass.

---

### Task 13: Simplify `TryGetExistingContext`

**Files:**
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs:161-188`

- [ ] **Step 1: Delete the redundant pre-scan**

In `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`, replace the body of `TryGetExistingContext` (lines 161–188):

```csharp
public static bool TryGetExistingContext(Dictionary<string, string> headers, out ActivityContext context)
{
    if (headers == null)
    {
        context = default;
        return false;
    }

    bool hasHeaders = false;
    foreach (string header in DistributedContextPropagator.Current.Fields)
    {
        if (headers.ContainsKey(header))
        {
            hasHeaders = true;
            break;
        }
    }

    if (hasHeaders)
    {
        DistributedContextPropagator.Current.ExtractTraceIdAndState(headers, ExtractTraceIdAndState,
            out string? traceParent, out string? traceState);
        return ActivityContext.TryParse(traceParent, traceState, out context);
    }

    context = default;
    return false;
}
```

with:

```csharp
public static bool TryGetExistingContext(Dictionary<string, string> headers, out ActivityContext context)
{
    if (headers == null)
    {
        context = default;
        return false;
    }

    DistributedContextPropagator.Current.ExtractTraceIdAndState(
        headers, ExtractTraceIdAndState,
        out string? traceParent, out string? traceState);
    return ActivityContext.TryParse(traceParent, traceState, out context);
}
```

- [ ] **Step 2: Run telemetry tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~ServiceConnectActivitySource|FullyQualifiedName~Telemetry"`
Expected: all tests pass. Verify that both "headers with traceparent" and "headers without traceparent" paths still behave correctly.

---

### Task 14: Single-pass `HashSet` for startup type names

**Files:**
- Modify: `src/ServiceConnect/Bus.cs:242-247`

- [ ] **Step 1: Replace the LINQ chain**

In `src/ServiceConnect/Bus.cs`, replace lines 242–247:

```csharp
                messageTypeNames =
                [
                    .. _handlerReferences
                        .Select(h => h.MessageType.FullName!.Replace(".", string.Empty))
                        .Distinct()
                ];
```

with:

```csharp
                var typeNameSet = new HashSet<string>(_handlerReferences.Count);
                foreach (var h in _handlerReferences)
                    typeNameSet.Add(h.MessageType.FullName!.Replace(".", string.Empty));
                messageTypeNames = [.. typeNameSet];
```

- [ ] **Step 2: Run bus tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~BusTests"`
Expected: all tests pass.

- [ ] **Step 3: Run full unit-test suite**

Run: `dotnet test src/ServiceConnect.UnitTests`
Expected: all tests pass.

- [ ] **Step 4: Commit Tier 3 (items 8–10 together)**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer.cs src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs src/ServiceConnect/Bus.cs
git commit -m "perf: minor cleanups in producer headers, telemetry, bus startup"
```

---

## Pre-E2E verification

- [ ] **Step 1: Full solution build**

Run: `dotnet build`
Expected: success, zero warnings introduced.

- [ ] **Step 2: Full unit-test suite**

Run: `dotnet test src/ServiceConnect.UnitTests`
Expected: all tests pass.

- [ ] **Step 3: `gitnexus_detect_changes` pre-commit audit**

Run via the GitNexus MCP tool: `gitnexus_detect_changes({scope: "all"})`.
Expected: changed symbols are exactly those listed in the File Structure section. Investigate and report any unexpected impact.

---

### Task 15: Full E2E test suite via `sg docker`

**Files:** no modifications — this task is the acceptance gate for the branch.

- [ ] **Step 1: Run the complete E2E suite**

Run: `sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests'`

Expected:
- All tests pass (including the 7 tests converted to `TestPolling`).
- Wall-clock runtime for the suite should be materially shorter than the pre-change baseline because of the Task 11 polling changes.
- No transient failures. If a test flakes on the first run, rerun once — if it flakes again, treat it as a regression to investigate, not an intermittent infrastructure issue.

- [ ] **Step 2: Report outcomes**

Summarise pass/fail counts and wall-clock runtime. If any test fails, capture its name and failure output verbatim for follow-up. Do NOT declare the branch ready until this task reports clean.

---

## Self-Review Notes

- **Spec coverage:** Tasks 1–6 map to items 1–2 (copies on deserialize and stream paths). Task 7 → item 3. Task 8 → item 4. Task 9 → item 5. Task 10 → item 6. Task 11 → item 7. Task 12 → item 8. Task 13 → item 9. Task 14 → item 10. Task 15 → acceptance E2E gate. All 10 findings covered.
- **Wire-format invariant:** No serializer swap. Newtonsoft remains the only `IMessageSerializer`. JSON bytes produced and consumed are byte-identical with the pre-change implementation.
- **Handler-facing contract:** `IMessageBusReadStream.Read()` preserved for handler code that consumes raw bytes (verified at `StreamingTests.cs:136`, `StreamOutOfOrderTests.cs:140`). Only the dispatch path uses the new `ReadSequence()`.
- **Concurrency:** `InMemoryTimeoutStore.GetTimeoutsBatchAsync` now uses the write lock instead of the read lock because the sorted index is mutated during the walk.
- **Mongo `$facet` compatibility:** Requires MongoDB 3.4+; all supported deployments meet this per the spec.
