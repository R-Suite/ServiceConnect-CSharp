# Code Review Remediations v2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the 5 Critical and 18 High severity findings remaining from the exhaustive code review of ServiceConnect-CSharp, after verification confirmed they still exist in the current codebase.

**Architecture:** Tasks are ordered so that foundational changes (shared utilities, disposal fixes) come first, then consumer-facing changes (bus lifecycle, security). Each task produces a working build independently. Filter consolidation (Task 10) is last because it touches many files.

**Tech Stack:** .NET 10, C#, RabbitMQ.Client 7.2.1, xUnit, Moq

---

### Task 1: Fix Producer.Dispose() / DisposeAsync() disposal race (R-001)

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer.cs:190-216`

**Problem:** `Dispose()` sets `_disposed = true` synchronously, then fire-and-forgets `DisposeAsync()`. But `DisposeAsync()` checks `if (_disposed) return;` — since `Dispose()` already set it, `DisposeAsync()` exits without closing connection or channel.

- [ ] **Step 1: Fix `Dispose()` and `DisposeAsync()` coordination**

Replace `Dispose()` and `DisposeAsync()` in `Producer.cs` with a unified disposal pattern where `Dispose()` does NOT set `_disposed` before calling `DisposeAsync()`:

```csharp
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    _ = Task.Run(async () =>
    {
        try { await DisposeAsyncCore().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Error during fire-and-forget dispose"); }
    });
}

public async ValueTask DisposeAsync()
{
    if (_disposed) return;
    _disposed = true;
    await DisposeAsyncCore().ConfigureAwait(false);
}

private async Task DisposeAsyncCore()
{
    await DisposeModelAsync().ConfigureAwait(false);
    await DisposeConnectionInstanceAsync().ConfigureAwait(false);
}
```

Key change: `DisposeAsync()` no longer uses `_connectionSemaphore` for the disposed check (the flag is set by the caller). The actual cleanup is in `DisposeAsyncCore()` which is called by both paths. Remove the semaphore acquire/release in `DisposeAsync()`.

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v q`
Expected: all pass

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer.cs
git commit -m "fix: resolve Producer.Dispose()/DisposeAsync() disposal race condition (R-001)"
```

---

### Task 2: Fix Client.Dispose() channel leak and SpinWait busy-loop (R-002, R-031)

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Client.cs:267-314`

**Problem:** `Client.Dispose()` and `DisposeAsync()` both set `_model = null` without calling `_model.CloseAsync()` or `_model.Dispose()`. Also uses SpinWait CPU-burning loop for up to 5 seconds.

- [ ] **Step 1: Replace `Dispose()` and `DisposeAsync()` with proper channel cleanup and efficient wait**

Replace `Dispose()` (lines 267-288) and `DisposeAsync()` (lines 290-314) with:

```csharp
public void Dispose()
{
    var deadline = Environment.TickCount64 + 5000;
    while (Volatile.Read(ref _messagesBeingProcessed) > 0 && Environment.TickCount64 < deadline)
    {
        Thread.Sleep(50);
    }

    CloseChannel();

    if (_autoDelete && _model != null)
    {
        var model = _model;
        var queueName = _queueName;
        _ = Task.Run(async () =>
        {
            try { await model.QueueDeleteAsync(queueName + ".Retries").ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error deleting retry queue during dispose"); }
        });
    }
}

public async ValueTask DisposeAsync()
{
    var deadline = Environment.TickCount64 + 5000;
    while (Volatile.Read(ref _messagesBeingProcessed) > 0 && Environment.TickCount64 < deadline)
    {
        await Task.Delay(50).ConfigureAwait(false);
    }

    await CloseChannelAsync().ConfigureAwait(false);

    if (_autoDelete && _model != null)
    {
        try
        {
            _logger.LogDebug("Deleting retry queue");
            await _model.QueueDeleteAsync(_queueName + ".Retries").ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error deleting retry queue");
        }
    }
}

private void CloseChannel()
{
    if (_model == null) return;
    try
    {
        if (_model.IsOpen)
            _model.Close();
        _model.Dispose();
    }
    catch (ObjectDisposedException) { }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Error closing channel during dispose");
    }
    _model = null;
}

private async Task CloseChannelAsync()
{
    if (_model == null) return;
    try
    {
        if (_model.IsOpen)
            await _model.CloseAsync().ConfigureAwait(false);
        _model.Dispose();
    }
    catch (ObjectDisposedException) { }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Error closing channel during dispose");
    }
    _model = null;
}
```

Changes:
- SpinWait → `Thread.Sleep(50)` / `Task.Delay(50)` (efficient sleep instead of CPU spin)
- `_model = null` replaced with `CloseChannel()` / `CloseChannelAsync()` that properly close and dispose the channel
- Empty `catch { }` now logs the exception

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v q`
Expected: all pass

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Client.cs
git commit -m "fix: close RabbitMQ channel on dispose, replace SpinWait with sleep (R-002, R-031)"
```

---

### Task 3: Fix Bus.Dispose not disposing Producer (R-006)

**Files:**
- Modify: `src/ServiceConnect/Bus.cs:208-218`

**Problem:** `Bus.Dispose()` disposes consumer and send pipeline but never disposes `_producer`, leaking RabbitMQ connection/channel.

- [ ] **Step 1: Add producer disposal to `Bus.Dispose()`**

Change `Bus.cs` Dispose method from:

```csharp
public void Dispose()
{
    lock (_stateLock)
    {
        if (_disposed) return;
        _disposed = true;
    }

    StopConsuming();
    _sendPipeline.Dispose();
}
```

To:

```csharp
public void Dispose()
{
    lock (_stateLock)
    {
        if (_disposed) return;
        _disposed = true;
    }

    StopConsuming();
    _sendPipeline.Dispose();
    _producer?.Dispose();
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v q`
Expected: all pass

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect/Bus.cs
git commit -m "fix: dispose Producer in Bus.Dispose() to prevent connection leak (R-006)"
```

---

### Task 4: Extract HeaderDecoder utility (R-014)

**Files:**
- Create: `src/ServiceConnect.Interfaces/HeaderDecoder.cs`
- Modify: `src/ServiceConnect/Services/ConsumeContext.cs`
- Modify: `src/ServiceConnect/Services/Processors/ReplyProcessor.cs`
- Modify: `src/ServiceConnect/Services/Processors/StreamProcessor.cs`
- Modify: `src/ServiceConnect/Services/Processors/HandlerProcessor.cs`
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Client.cs`
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`

**Problem:** The pattern `value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : value?.ToString()` is copy-pasted 12+ times across the codebase.

- [ ] **Step 1: Create `HeaderDecoder.cs` in Interfaces project**

```csharp
using System.Text;

namespace ServiceConnect.Interfaces;

public static class HeaderDecoder
{
    public static string? Decode(object? value)
    {
        if (value is byte[] bytes)
            return Encoding.UTF8.GetString(bytes);
        return value?.ToString();
    }
}
```

- [ ] **Step 2: Update `ConsumeContext.cs` — replace private `DecodeHeaderValue` with `HeaderDecoder.Decode`**

Remove the private `DecodeHeaderValue` method (lines 38-43) and replace all 3 call sites (`MessageId`, `CorrelationId`, `ReplyAsync`) with `HeaderDecoder.Decode(value)`. Add `using ServiceConnect.Interfaces;` if not present.

- [ ] **Step 3: Update `ReplyProcessor.cs`**

Read file, find the byte[] decoding pattern (around line 17-19), replace with `HeaderDecoder.Decode(...)`.

- [ ] **Step 4: Update `StreamProcessor.cs`**

Read file, find each header decoding instance (lines 36, 42, 46, 60, 80), replace with `HeaderDecoder.Decode(...)`.

- [ ] **Step 5: Update `HandlerProcessor.cs`**

Read file, find header decoding (around lines 59-61), replace with `HeaderDecoder.Decode(...)`.

- [ ] **Step 6: Update `MessageDispatcher.cs`**

Read file, find header decoding (around lines 36-38), replace with `HeaderDecoder.Decode(...)`.

- [ ] **Step 7: Update `Client.cs`**

In `ProcessMessage`, lines 120 and 182 have `is byte[] ... ? Encoding.UTF8.GetString(...) : ...`. Replace with `HeaderDecoder.Decode(...)`.

- [ ] **Step 8: Update `ServiceConnectActivitySource.cs`**

In the header iteration loops in `Publish()`, `Consume()`, `Send()`, replace the inline decoding with `HeaderDecoder.Decode(...)`.

- [ ] **Step 9: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 10: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v q`
Expected: all pass

- [ ] **Step 11: Commit**

```bash
git add src/ServiceConnect.Interfaces/HeaderDecoder.cs src/ServiceConnect/Services/ConsumeContext.cs src/ServiceConnect/Services/Processors/ReplyProcessor.cs src/ServiceConnect/Services/Processors/StreamProcessor.cs src/ServiceConnect/Services/Processors/HandlerProcessor.cs src/ServiceConnect/Services/MessageDispatcher.cs src/ServiceConnect.Client.RabbitMQ/Client.cs src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs
git commit -m "refactor: extract HeaderDecoder utility to eliminate 12+ copy-pasted header decoding patterns (R-014)"
```

---

### Task 5: Fix deduplication filter unsafe cast (R-007)

**Files:**
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingFilter.cs`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingFilter.cs`

**Problem:** `(byte[])envelope.Headers["MessageId"]` throws `InvalidCastException` when header values are strings (which they are from Producer).

- [ ] **Step 1: Fix `IncomingFilter.cs` header value decoding**

Add `using ServiceConnect.Interfaces;` to the file.

Replace the unsafe cast at line 44:
```csharp
new Guid(Encoding.UTF8.GetString((byte[]) (envelope.Headers["MessageId"])))
```

With:
```csharp
new Guid(HeaderDecoder.Decode(envelope.Headers["MessageId"]) ?? string.Empty)
```

Also fix the Redelivered header at line 39 — if it uses `(byte[])` or `bool` cast, use `HeaderDecoder.Decode` instead.

- [ ] **Step 2: Fix `OutgoingFilter.cs` header value decoding**

Same pattern at line 71. Replace:
```csharp
new Guid(Encoding.UTF8.GetString((byte[]) envelope.Headers["MessageId"]))
```

With:
```csharp
new Guid(HeaderDecoder.Decode(envelope.Headers["MessageId"]) ?? string.Empty)
```

- [ ] **Step 3: Add project reference if needed**

Check if `ServiceConnect.Filters.MessageDeduplication.csproj` already references `ServiceConnect.Interfaces`. If not, add:
```xml
<ProjectReference Include="..\..\..\src\ServiceConnect.Interfaces\ServiceConnect.Interfaces.csproj" />
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 5: Run dedup filter tests if they exist**

Run: `dotnet test filters/ServiceConnect.Filters.MessageDeduplication/ -v q` (if test project exists)
Expected: all pass

- [ ] **Step 6: Commit**

```bash
git add filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingFilter.cs filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingFilter.cs
git commit -m "fix: replace unsafe byte[] cast with HeaderDecoder in dedup filters (R-007)"
```

---

### Task 6: Fix AggregatorProcessor data loss on execution failure (R-030)

**Files:**
- Modify: `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs:86-122`

**Problem:** `FlushAggregator` removes data from persistor inside the lock (lines 112-116), then calls `executeMethod.Invoke` outside the lock (line 121). If Execute throws, messages are lost.

- [ ] **Step 1: Move data removal after successful execution**

Change `FlushAggregator` method. Move `persistor.RemoveData` calls after `executeMethod.Invoke`:

```csharp
private void FlushAggregator(string aggregatorName, Type messageType, Type aggregatorBaseType)
{
    IList<object> rawMessages;
    System.Collections.IList typedList;
    object? aggregator;
    System.Reflection.MethodInfo? executeMethod;

    lock (_flushLock)
    {
        var persistor = serviceProvider.GetService<IAggregatorPersistor>();
        if (persistor == null) return;

        rawMessages = persistor.GetData(aggregatorName);
        if (rawMessages.Count == 0) return;

        var listType = typeof(List<>).MakeGenericType(messageType);
        typedList = (System.Collections.IList)Activator.CreateInstance(listType)!;
        foreach (var msg in rawMessages)
            typedList.Add(msg);

        aggregator = serviceProvider.GetService(aggregatorBaseType);
        if (aggregator == null) return;

        executeMethod = aggregatorBaseType.GetMethod("Execute");

        if (_timers.TryRemove(aggregatorName, out var activeTimer))
            activeTimer.Dispose();
    }

    executeMethod?.Invoke(aggregator, [typedList]);

    // Remove data only after successful execution
    var postFlushPersistor = serviceProvider.GetService<IAggregatorPersistor>();
    if (postFlushPersistor != null)
    {
        foreach (var msg in rawMessages)
        {
            if (msg is Message m)
                postFlushPersistor.RemoveData(aggregatorName, m.CorrelationId);
        }
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v q`
Expected: all pass

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect/Services/Processors/AggregatorProcessor.cs
git commit -m "fix: remove aggregator data after successful execution, not before (R-030)"
```

---

### Task 7: Fix MessageBusWriteStream thread safety and MessageBusReadStream size limit (R-037, R-026)

**Files:**
- Modify: `src/ServiceConnect/Services/MessageBusWriteStream.cs:28-42`
- Modify: `src/ServiceConnect/Services/MessageBusReadStream.cs:15-42`

**Problem:** `_packetNumber` in WriteStream is not thread-safe. ReadStream has no size limit for stream reassembly.

- [ ] **Step 1: Fix `_packetNumber` thread safety in `MessageBusWriteStream.cs`**

Replace `_packetNumber++` with `Interlocked.Increment`:

Change field declaration from `private long _packetNumber;` to `private long _packetNumber;` (no change needed).

Change `WriteAsync` to capture the packet number atomically:

```csharp
public async Task WriteAsync(byte[] buffer, int offset, int count)
{
    ObjectDisposedException.ThrowIf(_closed, this);

    var packet = new byte[count];
    Array.Copy(buffer, offset, packet, 0, count);

    var packetNum = Interlocked.Increment(ref _packetNumber) - 1;

    var headers = new Dictionary<string, string>(_baseHeaders)
    {
        [HeaderKeys.PacketNumber] = packetNum.ToString()
    };

    await _producer.SendBytesAsync(_endpoint, packet, headers).ConfigureAwait(false);
}
```

Similarly in `CloseAsync`, capture atomically:

```csharp
public async Task CloseAsync()
{
    if (_closed) return;
    _closed = true;

    var packetNum = Interlocked.Read(ref _packetNumber);

    var headers = new Dictionary<string, string>(_baseHeaders)
    {
        [HeaderKeys.PacketNumber] = packetNum.ToString(),
        [HeaderKeys.LastPacketNumber] = packetNum.ToString()
    };

    await _producer.SendBytesAsync(_endpoint, [], headers).ConfigureAwait(false);
}
```

- [ ] **Step 2: Add size limit to `MessageBusReadStream.cs`**

Add a max total size constant and track cumulative bytes:

```csharp
using System.Collections.Concurrent;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public sealed class MessageBusReadStream : IMessageBusReadStream
{
    private const long MaxTotalStreamSize = 100 * 1024 * 1024; // 100 MB
    private readonly ConcurrentDictionary<long, byte[]> _packets = new();
    private long _totalBytesWritten;

    public string SequenceId { get; set; } = string.Empty;
    public long LastPacketNumber { get; set; } = -1;
    public MessageBusStreamComplete CompleteEventHandler { get; set; } = null!;
    public int HandlerCount { get; set; }

    public void Write(byte[] data, long packetNumber)
    {
        if (Interlocked.Add(ref _totalBytesWritten, data.Length) > MaxTotalStreamSize)
            throw new InvalidOperationException($"Stream exceeds maximum size of {MaxTotalStreamSize / (1024 * 1024)} MB.");
        _packets[packetNumber] = data;
    }

    public byte[] Read()
    {
        if (!IsComplete())
            throw new InvalidOperationException("Stream is not yet complete.");

        using var ms = new MemoryStream();
        for (long i = 0; i <= LastPacketNumber; i++)
        {
            if (_packets.TryGetValue(i, out var packet))
                ms.Write(packet, 0, packet.Length);
        }
        return ms.ToArray();
    }

    public bool IsComplete()
    {
        if (LastPacketNumber < 0) return false;
        for (long i = 0; i <= LastPacketNumber; i++)
        {
            if (!_packets.ContainsKey(i)) return false;
        }
        return true;
    }
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 4: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v q`
Expected: all pass

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/MessageBusWriteStream.cs src/ServiceConnect/Services/MessageBusReadStream.cs
git commit -m "fix: thread-safe packet numbering and stream size limit (R-037, R-026)"
```

---

### Task 8: Fix CacheProvider TOCTOU and other thread-safety issues (R-035, R-040, R-041)

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/CacheProvider.cs:143-155`
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs:311-313`
- Modify: `src/ServiceConnect/Configuration/QueueConfiguration.cs:15-30`

- [ ] **Step 1: Fix `CacheProvider.TryPurgeItem` — replace `ContainsKey` + indexer with `TryGetValue`**

In `CacheProvider.cs`, replace the `TryPurgeItem` method's check-then-use pattern:

Change from:
```csharp
if (_slidingTime.ContainsKey(key!))
{
    if (!_slidingTime[key!].CanExpire(...))
```

To:
```csharp
if (_slidingTime.TryGetValue(key!, out var details))
{
    if (!details.CanExpire(...))
```

- [ ] **Step 2: Fix `MongoDbProcessManagerFinder._timeoutIndexEnsured` — use `volatile`**

In `MongoDbProcessManagerFinder.cs`, change:
```csharp
private bool _timeoutIndexEnsured;
```
To:
```csharp
private volatile bool _timeoutIndexEnsured;
```

- [ ] **Step 3: Fix `QueueConfiguration.AddQueueMapping` — use `ConcurrentDictionary` or document single-threaded use**

Change `QueueMappings` from `Dictionary<string, IList<string>>` to `ConcurrentDictionary<string, IList<string>>` and use thread-safe operations:

Change field declaration from:
```csharp
public IDictionary<string, IList<string>> QueueMappings { get; set; } = new Dictionary<string, IList<string>>();
```

To:
```csharp
public IDictionary<string, IList<string>> QueueMappings { get; set; } = new ConcurrentDictionary<string, IList<string>>();
```

Update `AddQueueMapping` to use `ConcurrentDictionary` operations:

```csharp
public void AddQueueMapping(Type messageType, string queue)
{
    string key = messageType.FullName!;
    var list = new List<string> { queue };
    ((ConcurrentDictionary<string, IList<string>>)QueueMappings).AddOrUpdate(
        key,
        list,
        (_, existing) => { existing.Add(queue); return existing; });
}
```

Do the same for the `IList<string>` overload.

- [ ] **Step 4: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 5: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v q`
Expected: all pass

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/CacheProvider.cs src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs src/ServiceConnect/Configuration/QueueConfiguration.cs
git commit -m "fix: resolve TOCTOU and thread-safety issues in CacheProvider, MongoDbProcessManagerFinder, QueueConfiguration (R-035, R-040, R-041)"
```

---

### Task 9: Security hardening — SSL validation, gzip size limit, exception leakage (R-004, R-005, R-025, R-082)

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/SslConfigurationBuilder.cs`
- Modify: `src/ServiceConnect/Configuration/TransportConfiguration.cs`
- Modify: `filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/IncomingGzipCompressionFilter.cs`

- [ ] **Step 1: Add warning logging for dangerous `AcceptablePolicyErrors` values (R-005)**

In `SslConfigurationBuilder.BuildSslOptions()`, add a warning after building the options:

```csharp
public static SslOption BuildSslOptions(ITransportConfiguration transportSettings)
{
    var sslOption = new SslOption
    {
        Enabled = true,
        ServerName = transportSettings.ServerName,
        CertPath = transportSettings.CertPath,
        AcceptablePolicyErrors = transportSettings.AcceptablePolicyErrors,
        Certs = transportSettings.Certs,
        Version = transportSettings.SslProtocol,
        CertPassphrase = transportSettings.CertPassphrase
    };

    if (transportSettings.CertificateValidationCallback != null)
        sslOption.CertificateValidationCallback = transportSettings.CertificateValidationCallback;

    if (transportSettings.AcceptablePolicyErrors != SslPolicyErrors.None)
    {
        // Log a warning — this is a security concern. Consumers should ensure
        // this is only used in development/testing, never in production.
        // The caller can provide a logger via the transport configuration
        // or this can be observed via health checks.
    }

    return sslOption;
}
```

Since `SslConfigurationBuilder` is a static class without a logger, the best approach is to document the risk. Add a XML doc comment on `ITransportConfiguration.AcceptablePolicyErrors`:

In `TransportConfiguration.cs`:
```csharp
/// <summary>
/// Gets or sets the SSL policy errors that are acceptable.
/// WARNING: Setting any value other than <see cref="SslPolicyErrors.None"/> weakens TLS security
/// and should only be used in development/testing environments.
/// </summary>
public SslPolicyErrors AcceptablePolicyErrors { get; set; } = SslPolicyErrors.None;
```

- [ ] **Step 2: Add XML doc warning on `CertificateValidationCallback` (R-004)**

In `TransportConfiguration.cs`, add:
```csharp
/// <summary>
/// Gets or sets a custom certificate validation callback.
/// SECURITY WARNING: A callback that unconditionally returns <c>true</c> bypasses
/// all TLS certificate validation, enabling man-in-the-middle attacks.
/// Only use this in development/testing with full understanding of the risks.
/// </summary>
public RemoteCertificateValidationCallback? CertificateValidationCallback { get; set; }
```

- [ ] **Step 3: Add gzip decompression size limit (R-025)**

In `IncomingGzipCompressionFilter.cs`, add a max decompressed size check:

```csharp
using System.IO.Compression;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.GzipCompression;

public class IncomingGzipCompressionFilter : IFilter
{
    private const int MaxDecompressedSize = 10 * 1024 * 1024; // 10 MB

    public IBus Bus { get; set; } = null!;

    public bool Process(Envelope envelope)
    {
        using var compressedMessageMemoryStream = new MemoryStream(envelope.Body);
        using var messageMemoryStream = new MemoryStream();
        using (var gzipStream = new GZipStream(compressedMessageMemoryStream, CompressionMode.Decompress))
        {
            gzipStream.CopyTo(messageMemoryStream);
            if (messageMemoryStream.Length > MaxDecompressedSize)
                throw new InvalidOperationException($"Decompressed message exceeds maximum size of {MaxDecompressedSize / (1024 * 1024)} MB.");
        }
        envelope.Body = messageMemoryStream.ToArray();
        return true;
    }
}
```

Also replace `MemoryStreamUtilities.CopyTo` with `Stream.CopyTo` (which also fixes R-065).

- [ ] **Step 4: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/SslConfigurationBuilder.cs src/ServiceConnect/Configuration/TransportConfiguration.cs filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/IncomingGzipCompressionFilter.cs
git commit -m "fix: add security warnings for SSL bypass, gzip decompression size limit (R-004, R-005, R-025)"
```

---

### Task 10: Remove MemoryStreamUtilities and use Stream.CopyTo (R-065)

**Files:**
- Modify: `filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/OutgoingGzipCompressionFilter.cs`
- Delete: `filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/MemoryStreamUtilities.cs`

- [ ] **Step 1: Update `OutgoingGzipCompressionFilter.cs` to use `Stream.CopyTo`**

Read the file, find any usage of `MemoryStreamUtilities.CopyTo`, replace with `source.CopyTo(destination)`.

- [ ] **Step 2: Delete `MemoryStreamUtilities.cs`**

If `IncomingGzipCompressionFilter.cs` (updated in Task 9) already uses `Stream.CopyTo` and no other file references `MemoryStreamUtilities`, delete the file.

- [ ] **Step 3: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 4: Commit**

```bash
git add filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/OutgoingGzipCompressionFilter.cs
git rm filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/MemoryStreamUtilities.cs
git commit -m "refactor: remove MemoryStreamUtilities, use Stream.CopyTo (R-065)"
```

---

### Task 11: Fix InMemory dedup filter creating new persistor per call (R-033)

**Files:**
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilterInMemory.cs`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilterInMemory.cs`

**Problem:** `new MessageDeduplicationPersistorInMemory()` is created on every `Process()` call. Since the InMemory persistor uses a static ConcurrentDictionary, data technically persists, but it's wasteful and fragile.

- [ ] **Step 1: Cache the persistor instance using `Lazy<T>`**

In `IncomingDeduplicationFilterInMemory.cs`, replace:

```csharp
public bool Process(Envelope envelope)
{
    var incomingFilter = new IncomingFilter(new MessageDeduplicationPersistorInMemory());
    return incomingFilter.Process(envelope);
}
```

With:

```csharp
private static readonly Lazy<IncomingFilter> _incomingFilter = new(() =>
    new IncomingFilter(new MessageDeduplicationPersistorInMemory()));

public bool Process(Envelope envelope)
{
    return _incomingFilter.Value.Process(envelope);
}
```

- [ ] **Step 2: Same fix for `OutgoingDeduplicationFilterInMemory.cs`**

Replace:
```csharp
var outgoingFilter = new OutgoingFilter(new MessageDeduplicationPersistorInMemory());
```

With:
```csharp
private static readonly Lazy<OutgoingFilter> _outgoingFilter = new(() =>
    new OutgoingFilter(new MessageDeduplicationPersistorInMemory()));
```

And use `_outgoingFilter.Value.Process(envelope)`.

- [ ] **Step 3: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 4: Commit**

```bash
git add filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilterInMemory.cs filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilterInMemory.cs
git commit -m "fix: cache InMemory dedup persistor instance instead of creating per call (R-033)"
```

---

### Task 12: Fix dedup filter static field race conditions (R-008)

**Files:**
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilterMongoDb.cs`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilterMongoDbSsl.cs`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilterRedis.cs`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilterMongoDb.cs`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilterMongoDbSsl.cs`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilterRedis.cs`

**Problem:** All 6 files use `if (null == _incomingFilter) { _incomingFilter = new ...; }` without locking or volatile.

- [ ] **Step 1: Replace null-check-then-assign with `Lazy<T>` in all 6 files**

For each file, replace the pattern:
```csharp
private static IncomingFilter _incomingFilter;

public bool Process(Envelope envelope)
{
    if (null == _incomingFilter)
    {
        _incomingFilter = new IncomingFilter(new MessageDeduplicationPersistorMongoDb());
    }
    return _incomingFilter.Process(envelope);
}
```

With:
```csharp
private static readonly Lazy<IncomingFilter> _incomingFilter = new(() =>
    new IncomingFilter(new MessageDeduplicationPersistorMongoDb()));

public bool Process(Envelope envelope)
{
    return _incomingFilter.Value.Process(envelope);
}
```

Apply the same pattern to all 6 files (3 Incoming, 3 Outgoing), matching each file's specific persistor type.

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Commit**

```bash
git add filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/
git commit -m "fix: replace unsafe static lazy init with Lazy<T> in dedup filters (R-008)"
```

---

### Task 13: Add exponential backoff with jitter to Retry (R-079)

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Retry.cs`
- Modify: `src/ServiceConnect.UnitTests/RetryTests.cs`

- [ ] **Step 1: Add exponential backoff with jitter to `Retry.DoAsync`**

Replace `Retry.cs` with:

```csharp
namespace ServiceConnect.Client.RabbitMQ;

public static class Retry
{
    private static readonly Random Jitter = new();

    public static async Task DoAsync(Func<Task> action, Func<Exception, Task> exceptionAction, TimeSpan retryInterval, int retryCount)
    {
        List<Exception> exceptions = [];

        for (int retry = 0; retry < retryCount; retry++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
                try
                {
                    await exceptionAction(ex).ConfigureAwait(false);
                }
                catch (Exception callbackEx)
                {
                    exceptions.Add(callbackEx);
                }

                var delay = CalculateDelay(retryInterval, retry);
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }

        throw new AggregateException(exceptions);
    }

    public static async Task<T> DoAsync<T>(Func<Task<T>> action, Func<Exception, Task> exceptionAction, TimeSpan retryInterval, int retryCount)
    {
        List<Exception> exceptions = [];

        for (int retry = 0; retry < retryCount; retry++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
                try
                {
                    await exceptionAction(ex).ConfigureAwait(false);
                }
                catch (Exception callbackEx)
                {
                    exceptions.Add(callbackEx);
                }

                var delay = CalculateDelay(retryInterval, retry);
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }

        throw new AggregateException(exceptions);
    }

    private static TimeSpan CalculateDelay(TimeSpan baseInterval, int retryAttempt)
    {
        var exponentialDelay = TimeSpan.FromMilliseconds(baseInterval.TotalMilliseconds * Math.Pow(2, retryAttempt));
        var jitterMs = Jitter.Next(0, (int)Math.Min(baseInterval.TotalMilliseconds, 1000));
        var totalDelay = exponentialDelay + TimeSpan.FromMilliseconds(jitterMs);
        return TimeSpan.FromMilliseconds(Math.Min(totalDelay.TotalMilliseconds, TimeSpan.FromMinutes(5).TotalMilliseconds));
    }
}
```

- [ ] **Step 2: Update RetryTests if needed**

Check if any tests assert on specific retry timing. If they do, update to account for the new backoff. Tests that just verify "retries N times" should still pass.

- [ ] **Step 3: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 4: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter RetryTests -v q`
Expected: all pass

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Retry.cs src/ServiceConnect.UnitTests/RetryTests.cs
git commit -m "feat: add exponential backoff with jitter to Retry (R-079)"
```

---

### Task 14: Add SslConfigurationBuilder null guards (R-036)

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/SslConfigurationBuilder.cs`

- [ ] **Step 1: Add null checks for `ServerName` and `CertPath` in `BuildSslOptions`**

Replace the `BuildSslOptions` method with:

```csharp
public static SslOption BuildSslOptions(ITransportConfiguration transportSettings)
{
    if (string.IsNullOrWhiteSpace(transportSettings.ServerName))
        throw new ArgumentException("ServerName is required when SSL is enabled. Configure ITransportConfiguration.ServerName.", nameof(transportSettings));

    var sslOption = new SslOption
    {
        Enabled = true,
        ServerName = transportSettings.ServerName!,
        CertPath = transportSettings.CertPath ?? string.Empty,
        AcceptablePolicyErrors = transportSettings.AcceptablePolicyErrors,
        Certs = transportSettings.Certs,
        Version = transportSettings.SslProtocol,
        CertPassphrase = transportSettings.CertPassphrase
    };

    if (transportSettings.CertificateValidationCallback != null)
        sslOption.CertificateValidationCallback = transportSettings.CertificateValidationCallback;

    return sslOption;
}
```

- [ ] **Step 2: Update `SslConfigurationBuilderTests` if null ServerName was previously valid**

If any tests pass null ServerName, update them to pass a valid value or test that null throws.

- [ ] **Step 3: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 4: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter SslConfigurationBuilderTests -v q`
Expected: all pass

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/SslConfigurationBuilder.cs src/ServiceConnect.UnitTests/SslConfigurationBuilderTests.cs
git commit -m "fix: add null guard for ServerName in SslConfigurationBuilder (R-036)"
```

---

### Task 15: Add XML documentation to core public interfaces (R-029)

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IBus.cs`
- Modify: `src/ServiceConnect.Interfaces/IFilter.cs`
- Modify: `src/ServiceConnect.Interfaces/IFilterPipeline.cs`
- Modify: `src/ServiceConnect.Interfaces/IProducer.cs`
- Modify: `src/ServiceConnect.Interfaces/IConsumer.cs`
- Modify: `src/ServiceConnect.Interfaces/IMessageTypeRegistry.cs`
- Modify: `src/ServiceConnect.Interfaces/IMessageDispatcher.cs`

- [ ] **Step 1: Add XML docs to `IBus`**

```csharp
namespace ServiceConnect.Interfaces;

/// <summary>
/// The core message bus interface for publishing, sending, and consuming messages.
/// </summary>
public interface IBus : IDisposable
{
    /// <summary>
    /// Publishes a message to all subscribers of the message type.
    /// </summary>
    Task PublishAsync<T>(T message, PublishOptions? options = null) where T : Message;

    /// <summary>
    /// Sends a message to a specific endpoint or to the configured queue mapping.
    /// </summary>
    Task SendAsync<T>(T message, SendOptions? options = null) where T : Message;

    /// <summary>
    /// Sends a request and waits for a single reply.
    /// </summary>
    Task<TReply> SendRequestAsync<T, TReply>(T message, RequestOptions? options = null)
        where T : Message where TReply : Message;

    /// <summary>
    /// Sends a request and waits for multiple replies from all respondents.
    /// </summary>
    Task<IList<TReply>> SendRequestMultiAsync<T, TReply>(T message, RequestOptions? options = null)
        where T : Message where TReply : Message;

    /// <summary>
    /// Publishes a request and invokes a callback for each reply received.
    /// </summary>
    Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null)
        where TRequest : Message where TReply : Message;

    /// <summary>
    /// Routes a message through a series of destinations using a routing slip.
    /// </summary>
    Task RouteAsync<T>(T message, IList<string> destinations) where T : Message;

    /// <summary>
    /// Creates a streaming connection for sending large messages in chunks.
    /// </summary>
    IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message;

    /// <summary>
    /// Starts consuming messages from the configured queue.
    /// </summary>
    Task StartConsumingAsync();

    /// <summary>
    /// Stops consuming messages and disposes the consumer.
    /// </summary>
    void StopConsuming();

    /// <summary>
    /// Gets whether the bus is currently consuming messages.
    /// </summary>
    bool IsConnected { get; }
}
```

- [ ] **Step 2: Add XML docs to `IFilter`, `IFilterPipeline`, `IProducer`, `IConsumer`, `IMessageTypeRegistry`, `IMessageDispatcher`**

Add `<summary>` docs to each interface and its members. Key points:
- `IFilter.Process` returns `true` to stop the pipeline, `false` to continue
- `IFilterPipeline.ExecuteOutgoingFilters` returns `true` if a filter blocked the message
- `IMessageTypeRegistry` resolves type names from a safe registry (no `Type.GetType` fallback)

- [ ] **Step 3: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Interfaces/IBus.cs src/ServiceConnect.Interfaces/IFilter.cs src/ServiceConnect.Interfaces/IFilterPipeline.cs src/ServiceConnect.Interfaces/IProducer.cs src/ServiceConnect.Interfaces/IConsumer.cs src/ServiceConnect.Interfaces/IMessageTypeRegistry.cs src/ServiceConnect.Interfaces/IMessageDispatcher.cs
git commit -m "docs: add XML documentation to core public interfaces (R-029)"
```

---

### Task 16: Fix GzipCompressionFilter missing gzip header check (R-046)

**Files:**
- Modify: `filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/IncomingGzipCompressionFilter.cs`

- [ ] **Step 1: Add gzip magic byte check before decompression**

In `IncomingGzipCompressionFilter.cs`, add a check for the gzip magic bytes (`0x1f 0x8b`) before attempting decompression. If the body doesn't start with these bytes, pass it through unchanged:

```csharp
public bool Process(Envelope envelope)
{
    // Check for gzip magic bytes before attempting decompression
    if (envelope.Body.Length < 2 || envelope.Body[0] != 0x1f || envelope.Body[1] != 0x8b)
        return true;

    using var compressedMessageMemoryStream = new MemoryStream(envelope.Body);
    using var messageMemoryStream = new MemoryStream();
    using (var gzipStream = new GZipStream(compressedMessageMemoryStream, CompressionMode.Decompress))
    {
        gzipStream.CopyTo(messageMemoryStream);
        if (messageMemoryStream.Length > MaxDecompressedSize)
            throw new InvalidOperationException($"Decompressed message exceeds maximum size of {MaxDecompressedSize / (1024 * 1024)} MB.");
    }
    envelope.Body = messageMemoryStream.ToArray();
    return true;
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Commit**

```bash
git add filters/ServiceConnect.Filters.GzipCompression/ServiceConnect.Filters.GzipCompression/IncomingGzipCompressionFilter.cs
git commit -m "fix: check gzip magic bytes before decompression, skip non-gzip messages (R-046)"
```

---

## Findings NOT addressed in this plan (deferred or by-design)

| Finding | Reason |
|---------|--------|
| R-009 (Service Locator) | Inherent to message dispatch design; full refactor requires interface changes |
| R-010 (Layering violation) | Requires moving `IMessageTypeRegistry` to Interfaces; large project structure change |
| R-011/R-012/R-013 (ISP violations) | Interface segregation requires breaking API changes; separate future work |
| R-016 (Missing CancellationToken) | Requires changing all public async APIs — breaking change; needs major version bump |
| R-017/R-018 (Dedup persistor catch-and-swallow) | In filter project using Common.Logging; async fix requires IFilter interface change |
| R-020/R-021 (SRP: ProcessManagerProcessor, Client) | Large refactors that change internal structure; separate future work |
| R-022 (Dedup filter combinatorial explosion) | Major filter project restructuring; separate future work |
| R-027 (MongoDbSsl manual parsing) | Requires full rewrite of MongoDB SSL dedup filter; separate future work |
| R-028 (Zero unit test coverage) | Large effort; separate future work focused on test coverage |
| R-032 (DeduplicationFilterSettings singleton) | Requires DI migration of filter project; separate future work |
| R-034 (Bus.StartConsumingAsync race) | Mitigated by local consumer copy; full fix requires CancellationToken support (R-016) |
