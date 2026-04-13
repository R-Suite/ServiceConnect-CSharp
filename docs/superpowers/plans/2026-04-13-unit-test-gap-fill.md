# Unit Test Gap-Fill (R-028 Closeout) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add unit tests for the last five logic-bearing files in ServiceConnect that have no unit coverage, then mark R-028 Done.

**Architecture:** No production-code changes. Five new xUnit test files in `src/ServiceConnect.UnitTests/`, each targeting one existing class. Two new `ProjectReference` entries get added to the UnitTests csproj so it can see the MongoDb and Telemetry projects. A single docs commit closes R-028.

**Tech Stack:** xUnit 2.9.2, Moq 4.20.72, Microsoft.Extensions.Logging.Abstractions (`NullLogger<T>.Instance`), .NET 10 (`X509CertificateLoader`, `CertificateRequest.CreateSelfSigned`, `DistributedContextPropagator`, `ActivityListener`).

**Spec:** [docs/superpowers/specs/2026-04-13-unit-test-gap-fill-design.md](../specs/2026-04-13-unit-test-gap-fill-design.md)

**Conventions (observed from Groups C-1 through C-4):**
- File-scoped namespaces
- `NullLogger<T>.Instance` instead of `Mock<ILogger<T>>`
- `file class` fixtures at the bottom of test files where helpers are needed
- Moq for interface-based collaborators
- `Xunit` import

**Commit discipline** — each task ends with a commit. Task bodies stay on the branch `improvements-and-fixes`.

---

## Task 1: Wire project references for MongoDb and Telemetry

Before writing Mongo / Telemetry tests, the UnitTests project must reference both prod projects. This task is a preparatory edit isolated in its own commit so the two downstream test-task commits each contain only test code.

**Files:**
- Modify: [src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj](../../src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj)

- [ ] **Step 1: Add two `ProjectReference` lines**

Open `src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`. In the first `ItemGroup` (the one containing the existing `ProjectReference` entries), add:

```xml
<ProjectReference Include="..\ServiceConnect.Persistence.MongoDb\ServiceConnect.Persistence.MongoDb.csproj" />
<ProjectReference Include="..\ServiceConnect.Telemetry\ServiceConnect.Telemetry.csproj" />
```

The resulting `ItemGroup` should look like:

```xml
<ItemGroup>
    <ProjectReference Include="..\ServiceConnect.Interfaces\ServiceConnect.Interfaces.csproj" />
    <ProjectReference Include="..\ServiceConnect\ServiceConnect.csproj" />
    <ProjectReference Include="..\ServiceConnect.Client.RabbitMQ\ServiceConnect.Client.RabbitMQ.csproj" />
    <ProjectReference Include="..\ServiceConnect.Persistence.InMemory\ServiceConnect.Persistence.InMemory.csproj" />
    <ProjectReference Include="..\ServiceConnect.Persistence.MongoDb\ServiceConnect.Persistence.MongoDb.csproj" />
    <ProjectReference Include="..\ServiceConnect.Telemetry\ServiceConnect.Telemetry.csproj" />
</ItemGroup>
```

- [ ] **Step 2: Build the test project to verify the references resolve**

Run: `dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 3: Run the test suite to confirm no regression**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --nologo`
Expected: `Passed!  - Failed: 0, Passed: 290` (number may differ if baseline has shifted — the point is 0 failures).

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
git commit -m "$(cat <<'EOF'
test: wire MongoDb + Telemetry project references into UnitTests (R-028)

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: MessageBusWriteStreamTests

**Files:**
- Create: [src/ServiceConnect.UnitTests/MessageBusWriteStreamTests.cs](../../src/ServiceConnect.UnitTests/MessageBusWriteStreamTests.cs)

Production class under test: [src/ServiceConnect/Services/MessageBusWriteStream.cs](../../src/ServiceConnect/Services/MessageBusWriteStream.cs). It is `public sealed`. Its dependency is `IProducer` (defined in `ServiceConnect.Interfaces`). The relevant producer member is:

```csharp
Task SendBytesAsync(string endPoint, byte[] packet, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
```

- [ ] **Step 1: Create the test file with all 7 tests**

Create `src/ServiceConnect.UnitTests/MessageBusWriteStreamTests.cs`:

```csharp
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MessageBusWriteStreamTests
{
    private readonly Mock<IProducer> _producer = new();
    private readonly List<(string Endpoint, byte[] Payload, Dictionary<string, string>? Headers)> _sends = new();

    public MessageBusWriteStreamTests()
    {
        _producer
            .Setup(p => p.SendBytesAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, byte[], Dictionary<string, string>?, CancellationToken>((ep, bytes, headers, _) =>
                _sends.Add((ep, bytes, headers)))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task WriteAsync_PopulatesBaseHeaders_WithSequenceIdTypeNameAndMessageType()
    {
        await using var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));

        await stream.WriteAsync([1, 2, 3, 4], 0, 4);

        var headers = _sends.Single().Headers!;
        Assert.False(string.IsNullOrWhiteSpace(headers[HeaderKeys.SequenceId]));
        Assert.Equal(typeof(FakeStreamMsg).AssemblyQualifiedName, headers[HeaderKeys.FullTypeName]);
        Assert.Equal(typeof(FakeStreamMsg).FullName, headers[HeaderKeys.TypeName]);
        Assert.Equal(HeaderKeys.ByteStream, headers[HeaderKeys.MessageType]);
    }

    [Fact]
    public async Task WriteAsync_CopiesSubArray_UsingOffsetAndCount()
    {
        await using var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));
        var buffer = new byte[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 };

        await stream.WriteAsync(buffer, offset: 2, count: 4);

        var captured = _sends.Single().Payload;
        Assert.Equal(new byte[] { 12, 13, 14, 15 }, captured);
    }

    [Fact]
    public async Task WriteAsync_IncrementsPacketNumber_StartingAtZero()
    {
        await using var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));

        await stream.WriteAsync([1], 0, 1);
        await stream.WriteAsync([2], 0, 1);
        await stream.WriteAsync([3], 0, 1);

        Assert.Equal("0", _sends[0].Headers![HeaderKeys.PacketNumber]);
        Assert.Equal("1", _sends[1].Headers![HeaderKeys.PacketNumber]);
        Assert.Equal("2", _sends[2].Headers![HeaderKeys.PacketNumber]);
    }

    [Fact]
    public async Task WriteAsync_AfterClose_ThrowsObjectDisposedException()
    {
        var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));
        await stream.CloseAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.WriteAsync([1], 0, 1));
    }

    [Fact]
    public async Task CloseAsync_SendsEmptyPayloadWithLastPacketNumberHeader()
    {
        var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));
        await stream.WriteAsync([1], 0, 1);
        await stream.WriteAsync([2], 0, 1);

        await stream.CloseAsync();

        var closeSend = _sends.Last();
        Assert.Empty(closeSend.Payload);
        Assert.Equal("2", closeSend.Headers![HeaderKeys.PacketNumber]);
        Assert.Equal("2", closeSend.Headers![HeaderKeys.LastPacketNumber]);
    }

    [Fact]
    public async Task CloseAsync_IsIdempotent()
    {
        var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));

        await stream.CloseAsync();
        await stream.CloseAsync();

        // One close-marker send; no additional calls on second CloseAsync.
        Assert.Single(_sends);
    }

    [Fact]
    public async Task DisposeAsync_CallsCloseAsync()
    {
        var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));

        await stream.DisposeAsync();

        // DisposeAsync produced the close-marker send.
        Assert.Single(_sends);
        Assert.Empty(_sends[0].Payload);
        Assert.Contains(HeaderKeys.LastPacketNumber, _sends[0].Headers!.Keys);
    }
}

file class FakeStreamMsg : Message
{
    public FakeStreamMsg() : base(Guid.NewGuid()) { }
}
```

- [ ] **Step 2: Run the test file and confirm all 7 pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageBusWriteStreamTests" --nologo`
Expected: `Passed!  - Failed: 0, Passed: 7`.

- [ ] **Step 3: Run the full suite to ensure no regression**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --nologo`
Expected: `Passed!  - Failed: 0, Passed: 297`.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.UnitTests/MessageBusWriteStreamTests.cs
git commit -m "$(cat <<'EOF'
test: add MessageBusWriteStream unit tests (R-028)

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: DefaultProcessManagerPropertyMapperTests

**Files:**
- Create: [src/ServiceConnect.UnitTests/Processors/DefaultProcessManagerPropertyMapperTests.cs](../../src/ServiceConnect.UnitTests/Processors/DefaultProcessManagerPropertyMapperTests.cs)

Production class under test: [src/ServiceConnect/Services/Processors/DefaultProcessManagerPropertyMapper.cs](../../src/ServiceConnect/Services/Processors/DefaultProcessManagerPropertyMapper.cs). It is `internal`. The `ServiceConnect` csproj already declares `InternalsVisibleTo("ServiceConnect.UnitTests")`, so tests can instantiate it directly. Relevant interface method:

```csharp
public void ConfigureMapping<TProcessManagerData, TMessage>(
    Expression<Func<TProcessManagerData, object>> processManagerProperty,
    Expression<Func<TMessage, object>> messageExpression)
    where TProcessManagerData : IProcessManagerData
```

`ProcessManagerToMessageMap` exposes `MessageProp : Func<object, object>`, `MessageType : Type`, `PropertiesHierarchy : Dictionary<string, Type>`.

- [ ] **Step 1: Create the test file with all 4 tests**

Create `src/ServiceConnect.UnitTests/Processors/DefaultProcessManagerPropertyMapperTests.cs`:

```csharp
using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class DefaultProcessManagerPropertyMapperTests
{
    [Fact]
    public void ConfigureMapping_AddsMappingWithMessageType()
    {
        var mapper = new DefaultProcessManagerPropertyMapper();

        mapper.ConfigureMapping<FakePmData, FakePmMsg>(d => d.OrderId, m => m.OrderId);

        var mapping = Assert.Single(mapper.Mappings);
        Assert.Equal(typeof(FakePmMsg), mapping.MessageType);
    }

    [Fact]
    public void ConfigureMapping_UnwrapsUnaryExpression_ForValueTypeProperty()
    {
        // Guid -> object requires a boxing Convert expression; that means the body is a
        // UnaryExpression(Convert) around a MemberExpression. The mapper must unwrap it.
        var mapper = new DefaultProcessManagerPropertyMapper();

        mapper.ConfigureMapping<FakePmData, FakePmMsg>(d => d.OrderId, m => m.OrderId);

        var mapping = mapper.Mappings.Single();
        Assert.True(mapping.PropertiesHierarchy.ContainsKey(nameof(FakePmData.OrderId)));
        Assert.Equal(typeof(Guid), mapping.PropertiesHierarchy[nameof(FakePmData.OrderId)]);
    }

    [Fact]
    public void ConfigureMapping_HandlesDirectMemberExpression_ForReferenceTypeProperty()
    {
        // string -> object is reference-assignable, so no boxing Convert is synthesised;
        // the body is a MemberExpression directly.
        var mapper = new DefaultProcessManagerPropertyMapper();

        mapper.ConfigureMapping<FakePmData, FakePmMsg>(d => d.Customer, m => m.Customer);

        var mapping = mapper.Mappings.Single();
        Assert.True(mapping.PropertiesHierarchy.ContainsKey(nameof(FakePmData.Customer)));
        Assert.Equal(typeof(string), mapping.PropertiesHierarchy[nameof(FakePmData.Customer)]);
    }

    [Fact]
    public void ConfigureMapping_CompiledMessageFunc_ExtractsValueFromMessage()
    {
        var mapper = new DefaultProcessManagerPropertyMapper();
        mapper.ConfigureMapping<FakePmData, FakePmMsg>(d => d.Customer, m => m.Customer);

        var msg = new FakePmMsg(Guid.NewGuid()) { Customer = "Acme" };
        var value = mapper.Mappings.Single().MessageProp(msg);

        Assert.Equal("Acme", value);
    }
}

file class FakePmData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public Guid OrderId { get; set; }
    public string Customer { get; set; } = "";
}

file class FakePmMsg : Message
{
    public FakePmMsg(Guid c) : base(c) { }
    public Guid OrderId { get; set; }
    public string Customer { get; set; } = "";
}
```

- [ ] **Step 2: Run the test file and confirm all 4 pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~DefaultProcessManagerPropertyMapperTests" --nologo`
Expected: `Passed!  - Failed: 0, Passed: 4`.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --nologo`
Expected: `Passed!  - Failed: 0, Passed: 301`.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.UnitTests/Processors/DefaultProcessManagerPropertyMapperTests.cs
git commit -m "$(cat <<'EOF'
test: add DefaultProcessManagerPropertyMapper unit tests (R-028)

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: HeaderHelpersTests

**Files:**
- Create: [src/ServiceConnect.UnitTests/HeaderHelpersTests.cs](../../src/ServiceConnect.UnitTests/HeaderHelpersTests.cs)

Production class under test: [src/ServiceConnect.Client.RabbitMQ/HeaderHelpers.cs](../../src/ServiceConnect.Client.RabbitMQ/HeaderHelpers.cs). It is `internal static`. The `ServiceConnect.Client.RabbitMQ` csproj already declares `InternalsVisibleTo("ServiceConnect.UnitTests")`.

Public surface:

```csharp
public static void SetHeader<T>(IDictionary<string, object> headers, string key, T value);
public static Dictionary<string, object?> ToNullableHeaders(IDictionary<string, object> headers);
public static string GetErrorMessage(Exception exception);
```

- [ ] **Step 1: Create the test file with all 5 tests**

Create `src/ServiceConnect.UnitTests/HeaderHelpersTests.cs`:

```csharp
using ServiceConnect.Client.RabbitMQ;
using Xunit;

namespace ServiceConnect.UnitTests;

public class HeaderHelpersTests
{
    [Fact]
    public void SetHeader_WritesValue_WhenNonNull()
    {
        var headers = new Dictionary<string, object>();

        HeaderHelpers.SetHeader(headers, "X", "value");

        Assert.Equal("value", headers["X"]);
    }

    [Fact]
    public void SetHeader_OverwritesExistingKey_WhenNonNull()
    {
        var headers = new Dictionary<string, object> { ["X"] = "old" };

        HeaderHelpers.SetHeader(headers, "X", "new");

        Assert.Equal("new", headers["X"]);
    }

    [Fact]
    public void SetHeader_RemovesExistingKey_WhenNull()
    {
        var headers = new Dictionary<string, object> { ["X"] = "value" };

        HeaderHelpers.SetHeader<string?>(headers, "X", null);

        Assert.False(headers.ContainsKey("X"));
    }

    [Fact]
    public void SetHeader_WithNullOnMissingKey_NoOp()
    {
        var headers = new Dictionary<string, object>();

        HeaderHelpers.SetHeader<string?>(headers, "X", null);

        Assert.False(headers.ContainsKey("X"));
    }

    [Fact]
    public void ToNullableHeaders_PreservesAllEntriesAsNullableValues()
    {
        var source = new Dictionary<string, object>
        {
            ["A"] = "alpha",
            ["B"] = 42,
        };

        var result = HeaderHelpers.ToNullableHeaders(source);

        Assert.Equal(2, result.Count);
        Assert.Equal("alpha", result["A"]);
        Assert.Equal(42, result["B"]);
    }

    [Fact]
    public void GetErrorMessage_WalksInnerExceptionChain()
    {
        var inner = new InvalidOperationException("inner-msg");
        var middle = new ApplicationException("middle-msg", inner);
        var outer = new Exception("outer-msg", middle);

        var text = HeaderHelpers.GetErrorMessage(outer);

        Assert.Contains("outer-msg", text);
        Assert.Contains("middle-msg", text);
        Assert.Contains("inner-msg", text);
    }
}
```

- [ ] **Step 2: Run the test file and confirm all 6 pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~HeaderHelpersTests" --nologo`
Expected: `Passed!  - Failed: 0, Passed: 6`.

(The spec counted 5 but the implementation splits the "non-null" case into write-new and overwrite-existing — both are cheap to keep.)

- [ ] **Step 3: Run the full suite**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --nologo`
Expected: `Passed!  - Failed: 0, Passed: 307`.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.UnitTests/HeaderHelpersTests.cs
git commit -m "$(cat <<'EOF'
test: add HeaderHelpers unit tests (R-028)

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: MongoClientFactoryTests

**Files:**
- Create: [src/ServiceConnect.UnitTests/MongoClientFactoryTests.cs](../../src/ServiceConnect.UnitTests/MongoClientFactoryTests.cs)

Production class under test: [src/ServiceConnect.Persistence.MongoDb/MongoClientFactory.cs](../../src/ServiceConnect.Persistence.MongoDb/MongoClientFactory.cs). `public static`.

Options shapes:

```csharp
public sealed class MongoDbPersistenceOptions
{
    public string ConnectionString { get; set; } = "mongodb://localhost/";
    public string DatabaseName { get; set; } = "RMessageBusPersistentStore";
    public MongoDbSslOptions? Ssl { get; set; }
}

public sealed class MongoDbSslOptions
{
    public string? CertPath { get; set; }
    public string? CertPassphrase { get; set; }
    public SslProtocols SslProtocol { get; set; } = SslProtocols.Tls12;
    public bool AllowInsecureTls { get; set; }
    public bool CheckCertificateRevocation { get; set; } = true;
}
```

Production branches (net10.0 path, the only path the test project targets):

```csharp
var cert = string.IsNullOrEmpty(sslOptions.CertPassphrase)
    ? X509CertificateLoader.LoadCertificateFromFile(sslOptions.CertPath)       // public-only CER/DER
    : X509CertificateLoader.LoadPkcs12FromFile(sslOptions.CertPath, sslOptions.CertPassphrase);  // PFX
```

**Cert fixture strategy:** generate a self-signed cert in the test class constructor, export two temp files:
- `.cer` (public-only, DER) — consumed by the `LoadCertificateFromFile` branch (no passphrase)
- `.pfx` (PKCS#12, password-protected) — consumed by the `LoadPkcs12FromFile` branch (with passphrase)

Both temp files are deleted in `IDisposable.Dispose`.

- [ ] **Step 1: Create the test file with all 6 tests**

Create `src/ServiceConnect.UnitTests/MongoClientFactoryTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoClientFactoryTests : IDisposable
{
    private const string Passphrase = "testpass";

    private readonly string _certPath;   // DER-encoded public cert (no private key)
    private readonly string _pfxPath;    // PKCS#12 with private key, password-protected

    public MongoClientFactoryTests()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=serviceconnect-unittest",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));

        _certPath = Path.Combine(Path.GetTempPath(), $"sc-mongoclientfactory-{Guid.NewGuid():N}.cer");
        _pfxPath = Path.Combine(Path.GetTempPath(), $"sc-mongoclientfactory-{Guid.NewGuid():N}.pfx");

        File.WriteAllBytes(_certPath, cert.Export(X509ContentType.Cert));
        File.WriteAllBytes(_pfxPath, cert.Export(X509ContentType.Pfx, Passphrase));
    }

    public void Dispose()
    {
        if (File.Exists(_certPath)) File.Delete(_certPath);
        if (File.Exists(_pfxPath)) File.Delete(_pfxPath);
    }

    [Fact]
    public void Create_ReturnsClient_WithoutSsl_WhenSslNull()
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            Ssl = null
        };

        var client = MongoClientFactory.Create(options);

        Assert.False(client.Settings.UseTls);
    }

    [Fact]
    public void Create_EnablesUseTls_WhenSslConfigured()
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            Ssl = new MongoDbSslOptions()
        };

        var client = MongoClientFactory.Create(options);

        Assert.True(client.Settings.UseTls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_PropagatesAllowInsecureTls_FromOptions(bool allowInsecure)
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            Ssl = new MongoDbSslOptions { AllowInsecureTls = allowInsecure }
        };

        var client = MongoClientFactory.Create(options);

        Assert.Equal(allowInsecure, client.Settings.AllowInsecureTls);
    }

    [Fact]
    public void Create_WithoutCertPath_LeavesClientCertificatesUnset()
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            Ssl = new MongoDbSslOptions() // CertPath is null
        };

        var client = MongoClientFactory.Create(options);

        // When no cert is configured, the factory doesn't populate SslSettings at all.
        Assert.Null(client.Settings.SslSettings);
    }

    [Fact]
    public void Create_WithCertPath_NoPassphrase_LoadsPublicCert_AndSetsCheckCertificateRevocation()
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            Ssl = new MongoDbSslOptions
            {
                CertPath = _certPath,
                CheckCertificateRevocation = false
            }
        };

        var client = MongoClientFactory.Create(options);

        Assert.NotNull(client.Settings.SslSettings);
        var certs = client.Settings.SslSettings!.ClientCertificates!.Cast<X509Certificate>().ToList();
        Assert.Single(certs);
        Assert.False(client.Settings.SslSettings.CheckCertificateRevocation);
    }

    [Fact]
    public void Create_WithCertPath_AndPassphrase_LoadsPasswordProtectedCert()
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            Ssl = new MongoDbSslOptions
            {
                CertPath = _pfxPath,
                CertPassphrase = Passphrase,
                CheckCertificateRevocation = true
            }
        };

        var client = MongoClientFactory.Create(options);

        Assert.NotNull(client.Settings.SslSettings);
        var certs = client.Settings.SslSettings!.ClientCertificates!.Cast<X509Certificate>().ToList();
        Assert.Single(certs);
        Assert.True(client.Settings.SslSettings.CheckCertificateRevocation);
    }
}
```

- [ ] **Step 2: Run the test file and confirm all 7 pass (1 Theory expands to 2)**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoClientFactoryTests" --nologo`
Expected: `Passed!  - Failed: 0, Passed: 7`.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --nologo`
Expected: `Passed!  - Failed: 0, Passed: 314`.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.UnitTests/MongoClientFactoryTests.cs
git commit -m "$(cat <<'EOF'
test: add MongoClientFactory unit tests (R-028)

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: ServiceConnectActivitySourceTests

**Files:**
- Create: [src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs](../../src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs)

Production class under test: [src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs](../../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs). `public static`. Event-arg shapes:

```csharp
public class PublishEventArgs : OutgoingEventArgs { public string RoutingKey { get; init; } = string.Empty; }
public class SendEventArgs : OutgoingEventArgs { public string EndPoint { get; init; } = string.Empty; }
public class OutgoingEventArgs { public Message? Message { get; init; } public Dictionary<string, string> Headers { get; set; } = []; }
public class ConsumeEventArgs { public byte[] Message; public string Type; public IDictionary<string, object> Headers; }
```

**Listener fixture:** two distinct test classes.
1. `ServiceConnectActivitySourceTests` — installs an `ActivityListener` in its constructor, disposes in `IDisposable.Dispose`. Covers the "listener attached" behaviour.
2. `ServiceConnectActivitySource_NoListenerTests` — installs no listener. Covers the "returns null when no listener" behaviour. Isolated from the primary class so their lifecycles never overlap.

Both test classes assume xUnit's default behaviour: test classes in the same collection run serially, so an ActivityListener in one test class does not bleed into a separate test class with no listener (xUnit creates a fresh `ActivityListener`-free process within the same AppDomain; because `AddActivityListener` registers process-wide, the no-listener class must be placed in its *own* collection to avoid cross-class bleed).

Apply `[Collection]` attributes to isolate:

```csharp
[CollectionDefinition("ActivityListener", DisableParallelization = true)]
public class ActivityListenerCollection { }
```

Both test classes declare `[Collection("ActivityListener")]` so they cannot run in parallel; the "no listener" class runs after disposing any listener from the previous class.

- [ ] **Step 1: Create the test file with both classes and the collection definition**

Create `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;
using Xunit;
using static ServiceConnect.Telemetry.MessagingAttributes;

namespace ServiceConnect.UnitTests.Telemetry;

[CollectionDefinition("ActivityListener", DisableParallelization = true)]
public class ActivityListenerCollection { }

[Collection("ActivityListener")]
public sealed class ServiceConnectActivitySourceTests : IDisposable
{
    private readonly ActivityListener _listener;

    public ServiceConnectActivitySourceTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src =>
                src.Name == ServiceConnectActivitySource.PublishActivitySourceName
                || src.Name == ServiceConnectActivitySource.ConsumeActivitySourceName
                || src.Name == ServiceConnectActivitySource.SendActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        // Reset user-configurable enrichers in case a test set them.
        ServiceConnectActivitySource.Options.EnrichWithMessage = null;
        ServiceConnectActivitySource.Options.EnrichWithMessageBytes = null;
        _listener.Dispose();
    }

    // ---------------- Publish ----------------

    [Fact]
    public void Publish_WithRoutingKey_SetsNamedDestinationAndDisplayName()
    {
        var args = new PublishEventArgs
        {
            RoutingKey = "orders",
            Message = new Message(Guid.NewGuid()),
            Headers = { ["MessageId"] = "msg-1" }
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.NotNull(activity);
        Assert.Equal("orders publish", activity!.DisplayName);
        Assert.Equal("orders", activity.GetTagItem(MessagingDestination));
        Assert.Equal("orders", activity.GetTagItem(MessagingDestinationRoutingKey));
        Assert.Equal("rabbitmq", activity.GetTagItem(MessagingSystem));
        Assert.Equal("publish", activity.GetTagItem(MessagingOperation));
        Assert.Equal("msg-1", activity.GetTagItem(MessageId));
    }

    [Fact]
    public void Publish_WithoutRoutingKey_MarksDestinationAnonymous()
    {
        var args = new PublishEventArgs
        {
            RoutingKey = "",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.NotNull(activity);
        Assert.Equal("anonymous publish", activity!.DisplayName);
        Assert.Equal("true", activity.GetTagItem(MessagingDestinationAnonymous));
    }

    [Fact]
    public void Publish_EnricherThrows_RecordsEnrichmentException()
    {
        ServiceConnectActivitySource.Options.EnrichWithMessage =
            (_, _) => throw new InvalidOperationException("boom");

        var args = new PublishEventArgs
        {
            RoutingKey = "orders",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.NotNull(activity);
        Assert.Equal("boom", activity!.GetTagItem("enrichment.exception"));
    }

    // ---------------- Consume ----------------

    [Fact]
    public void Consume_SetsMessagingTags_AndDestinationFromHeader()
    {
        var args = new ConsumeEventArgs
        {
            Message = new byte[] { 1, 2, 3 },
            Headers = new Dictionary<string, object>
            {
                ["DestinationAddress"] = Encoding.UTF8.GetBytes("svc.inbox"),
                ["MessageId"] = Encoding.UTF8.GetBytes("msg-42")
            }
        };

        using var activity = ServiceConnectActivitySource.Consume(args);

        Assert.NotNull(activity);
        Assert.Equal("svc.inbox receive", activity!.DisplayName);
        Assert.Equal("svc.inbox", activity.GetTagItem(MessagingDestination));
        Assert.Equal("msg-42", activity.GetTagItem(MessageId));
        Assert.Equal("rabbitmq", activity.GetTagItem(MessagingSystem));
        Assert.Equal("receive", activity.GetTagItem(MessagingOperation));
        Assert.Equal(3, activity.GetTagItem(MessagingBodySize));
    }

    [Fact]
    public void Consume_WithoutDestinationHeader_MarksAnonymous()
    {
        var args = new ConsumeEventArgs
        {
            Message = Array.Empty<byte>(),
            Headers = new Dictionary<string, object>()
        };

        using var activity = ServiceConnectActivitySource.Consume(args);

        Assert.NotNull(activity);
        Assert.Equal("anonymous receive", activity!.DisplayName);
        Assert.Equal("true", activity.GetTagItem(MessagingDestinationAnonymous));
    }

    [Fact]
    public void Consume_ExtractsParentContext_FromTraceparentHeader()
    {
        // Build a valid W3C traceparent: 00-<32 hex traceId>-<16 hex spanId>-01
        var traceId = "0af7651916cd43dd8448eb211c80319c";
        var spanId = "b7ad6b7169203331";
        var traceparent = $"00-{traceId}-{spanId}-01";

        var args = new ConsumeEventArgs
        {
            Headers = new Dictionary<string, object>
            {
                ["traceparent"] = Encoding.UTF8.GetBytes(traceparent)
            }
        };

        using var activity = ServiceConnectActivitySource.Consume(args);

        Assert.NotNull(activity);
        Assert.Equal(traceId, activity!.TraceId.ToString());
        Assert.Equal(spanId, activity.ParentSpanId.ToString());
    }

    // ---------------- Send ----------------

    [Fact]
    public void Send_WithEndpoint_SetsNamedDestination()
    {
        var args = new SendEventArgs
        {
            EndPoint = "svc.queue",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Send(args);

        Assert.NotNull(activity);
        Assert.Equal("svc.queue publish", activity!.DisplayName);
        Assert.Equal("svc.queue", activity.GetTagItem(MessagingDestination));
    }

    [Fact]
    public void Send_WithoutEndpoint_MarksAnonymous()
    {
        var args = new SendEventArgs
        {
            EndPoint = "",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Send(args);

        Assert.NotNull(activity);
        Assert.Equal("anonymous publish", activity!.DisplayName);
        Assert.Equal("true", activity.GetTagItem(MessagingDestinationAnonymous));
    }

    // ---------------- TryGetExistingContext ----------------

    [Fact]
    public void TryGetExistingContext_WithTraceparent_ReturnsTrue_AndParsesContext()
    {
        var traceId = "0af7651916cd43dd8448eb211c80319c";
        var spanId = "b7ad6b7169203331";
        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = $"00-{traceId}-{spanId}-01"
        };

        var ok = ServiceConnectActivitySource.TryGetExistingContext(headers, out var ctx);

        Assert.True(ok);
        Assert.Equal(traceId, ctx.TraceId.ToString());
        Assert.Equal(spanId, ctx.SpanId.ToString());
    }

    [Fact]
    public void TryGetExistingContext_WithNullHeaders_ReturnsFalse()
    {
        var ok = ServiceConnectActivitySource.TryGetExistingContext(null!, out var ctx);

        Assert.False(ok);
        Assert.Equal(default, ctx);
    }

    [Fact]
    public void TryGetExistingContext_WithoutTraceHeaders_ReturnsFalse()
    {
        var headers = new Dictionary<string, string> { ["Unrelated"] = "v" };

        var ok = ServiceConnectActivitySource.TryGetExistingContext(headers, out var ctx);

        Assert.False(ok);
        Assert.Equal(default, ctx);
    }
}

[Collection("ActivityListener")]
public sealed class ServiceConnectActivitySource_NoListenerTests
{
    [Fact]
    public void Publish_ReturnsNull_WhenNoListeners()
    {
        var args = new PublishEventArgs
        {
            RoutingKey = "orders",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.Null(activity);
    }

    [Fact]
    public void Consume_ReturnsNull_WhenNoListeners()
    {
        var args = new ConsumeEventArgs();

        using var activity = ServiceConnectActivitySource.Consume(args);

        Assert.Null(activity);
    }

    [Fact]
    public void Send_ReturnsNull_WhenNoListeners()
    {
        var args = new SendEventArgs { EndPoint = "ep" };

        using var activity = ServiceConnectActivitySource.Send(args);

        Assert.Null(activity);
    }
}
```

The test file uses `using static ServiceConnect.Telemetry.MessagingAttributes;` so every asserted tag key is the same `const` string the prod code emits — no drift is possible.

- [ ] **Step 2: Run the test file and confirm all 13 pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ServiceConnectActivitySource" --nologo`
Expected: `Passed!  - Failed: 0, Passed: 13`.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --nologo`
Expected: `Passed!  - Failed: 0, Passed: 327`.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
git commit -m "$(cat <<'EOF'
test: add ServiceConnectActivitySource unit tests (R-028)

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Mark R-028 Done in remaining-issues.md

**Files:**
- Modify: [docs/remaining-issues.md](../../remaining-issues.md)

- [ ] **Step 1: Update the header paragraph and the R-028 row**

In `docs/remaining-issues.md`:

1. Replace the header paragraph that currently ends with "… completed in Group C-4 on 2026-04-13. Tackle remaining items after further discussion." with:

```markdown
Issues verified against source code on 2026-04-12. R-016/B-01 (CancellationToken) and R-034 (race condition) completed in Group C-1 on 2026-04-13. R-017/R-018 (async filter pipeline + fail-closed dedup) and R-032 (settings DI) completed in Group C-2 on 2026-04-13. R-020/R-021 (Client + ProcessManagerProcessor SRP refactor) completed in Group C-3 on 2026-04-13. R-009 (service locator + reflection in remaining three processors) completed in Group C-4 on 2026-04-13. R-028 (unit test gap-fill) completed in Group C-5 on 2026-04-13. All verified issues now resolved.
```

2. In the "From Deferred Issues (Confirmed Real)" table, replace the R-028 row:

```markdown
| R-028 | Testing | Zero unit test coverage | **Done** (Group C-5) — every logic-bearing non-integration-heavy class now has unit tests. Remaining untested files are either (a) broker/DB integration classes (Consumer/Producer/Connection/Client, MongoDbAggregatorPersistor/MongoDbProcessManagerFinder) covered comprehensively by E2E, or (b) logic-less POCOs (config classes, event-args, options). |
```

- [ ] **Step 2: Confirm the file still lints / renders cleanly**

Open the file and scan the updated rows. The table should remain well-formed.

- [ ] **Step 3: Commit**

```bash
git add docs/remaining-issues.md
git commit -m "$(cat <<'EOF'
docs: mark R-028 done (unit test gap-fill, Group C-5)

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Final verification

After Task 7:

- [ ] Run full unit-test suite: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --nologo` → expect 327/327 passing, 0 warnings.
- [ ] Confirm no production code changed: `git diff master..HEAD --stat -- 'src/**/*.cs' | grep -v UnitTests` should return no lines.
- [ ] Confirm [docs/remaining-issues.md](../../remaining-issues.md) has no verified-but-deferred rows left without a **Done** marker.

Total commits added by this plan: **7**.
