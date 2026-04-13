# Unit Test Gap-Fill (R-028 Closeout) — Design

**Status:** Approved 2026-04-13
**Scope:** Close out R-028 ("Zero unit test coverage") by adding unit tests for the remaining logic-bearing, non-integration-heavy files, then marking the ticket Done.
**Group:** C-5 (follows C-4 processor registry refactor)

## Goal

After this pass, every logic-bearing class that doesn't require a live broker or database has unit tests. Remaining unit-test gaps fall into two intentional categories:

1. **Integration-heavy classes** that are comprehensively exercised by the E2E suite (RabbitMQ `Client`/`Connection`/`Consumer`/`Producer`, Mongo `MongoDbAggregatorPersistor`/`MongoDbProcessManagerFinder`).
2. **Pure POCOs** with no behavior to verify (config classes with auto-properties only, event-args records, interface definitions).

R-028 is closed with an explicit note on both categories so the remaining untested surface is documented rather than undefined.

## In-Scope Files

| Target | New test file | Count |
|---|---|---|
| [MessageBusWriteStream.cs](../../src/ServiceConnect/Services/MessageBusWriteStream.cs) | [src/ServiceConnect.UnitTests/MessageBusWriteStreamTests.cs](../../src/ServiceConnect.UnitTests/MessageBusWriteStreamTests.cs) | ~7 |
| [DefaultProcessManagerPropertyMapper.cs](../../src/ServiceConnect/Services/Processors/DefaultProcessManagerPropertyMapper.cs) | [src/ServiceConnect.UnitTests/Processors/DefaultProcessManagerPropertyMapperTests.cs](../../src/ServiceConnect.UnitTests/Processors/DefaultProcessManagerPropertyMapperTests.cs) | ~4 |
| [HeaderHelpers.cs](../../src/ServiceConnect.Client.RabbitMQ/HeaderHelpers.cs) | [src/ServiceConnect.UnitTests/HeaderHelpersTests.cs](../../src/ServiceConnect.UnitTests/HeaderHelpersTests.cs) | ~5 |
| [MongoClientFactory.cs](../../src/ServiceConnect.Persistence.MongoDb/MongoClientFactory.cs) | [src/ServiceConnect.UnitTests/MongoClientFactoryTests.cs](../../src/ServiceConnect.UnitTests/MongoClientFactoryTests.cs) | ~6 |
| [ServiceConnectActivitySource.cs](../../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs) | [src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs](../../src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs) | ~11 |

**Expected total:** ~33 new tests across 5 files. Final state: 323/323 unit green.

## Out-of-Scope

Intentionally excluded:

- **RabbitMQ integration classes** (`Client`, `Connection`, `Consumer`, `Producer`) — broker-bound; covered by E2E against a live RabbitMQ. Unit tests would require heavy mock scaffolding and duplicate E2E verification.
- **Mongo persistence classes** (`MongoDbAggregatorPersistor`, `MongoDbProcessManagerFinder`) — DB-bound; covered by E2E against a live MongoDB.
- **Config POCOs** (`PersistenceConfiguration`, `PipelineConfiguration`) — only auto-properties with defaults; nothing to verify.
- **Event-args / options POCOs** (`ConsumeEventArgs`, `OutgoingEventArgs`, `PublishEventArgs`, `SendEventArgs`, `MongoDbPersistenceOptions`, `MongoDbSslOptions`, `ServiceConnectInstrumentationOptions`, `MemoryData`, `CacheItem`, `SlidingDetails`) — value carriers.
- **Interface definitions** — nothing to test.

## Architecture

All test files follow existing project conventions:

- xUnit (`[Fact]`, `[Theory]`, `[InlineData]`)
- Moq where an interface is needed as a test double
- `NullLogger<T>.Instance` in place of `Mock<ILogger<T>>` (matches the convention established in Groups C-3 / C-4; avoids Castle DynamicProxy's inability to proxy `ILogger<InternalType>` under strong-named assemblies)
- File-scoped namespaces matching the prod folder layout
- One test class per prod class

No new production-code changes. No csproj edits (every `InternalsVisibleTo` we need is already in place).

## Per-File Test Coverage

### MessageBusWriteStreamTests

Collaborator: `Mock<IProducer>` capturing `SendBytesAsync(endpoint, bytes, headers)` arguments.

| Test | Verifies |
|---|---|
| `Ctor_PopulatesBaseHeaders_WithSequenceIdTypeNameAndMessageType` | First `WriteAsync` carries `HeaderKeys.SequenceId` (non-empty GUID), `HeaderKeys.FullTypeName` (= messageType.AssemblyQualifiedName), `HeaderKeys.TypeName` (= messageType.FullName), `HeaderKeys.MessageType == HeaderKeys.ByteStream` |
| `WriteAsync_CopiesSubArray_UsingOffsetAndCount` | 10-byte buffer, offset=2, count=4 → captured payload is exactly those 4 bytes |
| `WriteAsync_IncrementsPacketNumber_StartingAtZero` | Three consecutive writes produce `PacketNumber` headers "0", "1", "2" |
| `WriteAsync_AfterClose_ThrowsObjectDisposedException` | Call `CloseAsync`, then `WriteAsync` throws |
| `CloseAsync_SendsEmptyPayloadWithLastPacketNumberHeader` | Close after 2 writes sends empty payload with both `PacketNumber` and `LastPacketNumber` headers set to "2" |
| `CloseAsync_IsIdempotent` | Two `CloseAsync` calls produce exactly one close-marker send |
| `DisposeAsync_CallsCloseAsync` | `DisposeAsync` produces the close-marker send |

### DefaultProcessManagerPropertyMapperTests

Uses `InternalsVisibleTo("ServiceConnect.UnitTests")` already set on `ServiceConnect.csproj`. No external deps.

| Test | Verifies |
|---|---|
| `ConfigureMapping_AddsMappingWithMessageType` | `Mappings` has one entry; `MessageType == typeof(TMsg)` |
| `ConfigureMapping_UnwrapsUnaryExpression_ForValueTypeProperty` | `data => data.SomeGuid` (Guid→object boxing = UnaryExpression(Convert)) captures property name + type correctly |
| `ConfigureMapping_HandlesDirectMemberExpression_ForReferenceTypeProperty` | `data => data.SomeString` is already MemberExpression; property name + type captured |
| `ConfigureMapping_CompiledMessageFunc_ExtractsValueFromMessage` | Invoke `map.MessageProp(messageInstance)` returns the expected boxed property value |

Helper types declared as file-scoped test fixtures (IProcessManagerData + message POCO).

### HeaderHelpersTests

Uses `InternalsVisibleTo("ServiceConnect.UnitTests")` already set on RabbitMQ csproj.

| Test | Verifies |
|---|---|
| `SetHeader_WritesValue_WhenNonNull` | Value added/overwritten |
| `SetHeader_RemovesExistingKey_WhenNull` | Existing key removed when value is null |
| `SetHeader_WithNullOnMissingKey_NoOp` | Null value on absent key does not throw; key stays absent |
| `ToNullableHeaders_ConvertsToNullableValueDictionary` | All k/v preserved; value type is `object?` |
| `GetErrorMessage_WalksInnerExceptionChain` | Outer + middle + inner messages all present in output |

### MongoClientFactoryTests

Class implements `IDisposable`. Ctor generates a self-signed cert using:

```csharp
using var rsa = RSA.Create(2048);
var req = new CertificateRequest("CN=serviceconnect-unittest", rsa,
    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var cert = req.CreateSelfSigned(
    DateTimeOffset.UtcNow.AddDays(-1),
    DateTimeOffset.UtcNow.AddDays(30));
```

Two temp PFX files are exported: one unprotected (`cert.Export(X509ContentType.Pfx)`), one protected (`cert.Export(X509ContentType.Pfx, "testpass")`), both to `Path.Combine(Path.GetTempPath(), $"mongoclientfactory-{Guid.NewGuid():N}.pfx")`. `Dispose()` deletes both.

| Test | Verifies |
|---|---|
| `Create_ReturnsClient_WithoutSsl_WhenSslNull` | `options.Ssl = null` → `client.Settings.UseTls == false` |
| `Create_EnablesUseTls_WhenSslConfigured` | `options.Ssl != null` (no cert path) → `client.Settings.UseTls == true` |
| `Create_PropagatesAllowInsecureTls_FromOptions` | `[Theory][InlineData(true)][InlineData(false)]` → `Settings.AllowInsecureTls` matches |
| `Create_WithoutCertPath_LeavesClientCertificatesNull` | No CertPath → `SslSettings` stays unpopulated |
| `Create_WithCertPath_LoadsCertificate_AndSetsCheckCertificateRevocation` | Generated PFX path → `SslSettings.ClientCertificates.Count == 1`, `CheckCertificateRevocation` matches option |
| `Create_WithCertPath_AndPassphrase_LoadsPasswordProtectedCert` | Password-protected PFX + matching `CertPassphrase` → cert loads |

**Target framework note:** the prod code has an `#if NET9_0_OR_GREATER` branch using `X509CertificateLoader` vs legacy `X509Certificate2` ctor. Tests do not exercise the ifdef directly — the runtime chooses the right path based on the TFM being tested. Cert generation itself uses APIs available on all target TFMs.

### ServiceConnectActivitySourceTests

Class implements `IDisposable`. Ctor installs an `ActivityListener` that matches any of the three activity-source names and samples everything:

```csharp
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
```

`Dispose()` calls `_listener.Dispose()`.

**Publish** (4):
| Test | Verifies |
|---|---|
| `Publish_ReturnsNull_WhenNoListeners` | Separate test class (no ctor-installed listener) calls `Publish(...)` and asserts null — distinct class so its lifecycle never shares an `ActivityListener` with the listener-fixture tests |
| `Publish_WithRoutingKey_SetsNamedDestinationAndDisplayName` | RoutingKey="orders" → `DisplayName == "orders publish"`, destination & routing-key tags set |
| `Publish_WithoutRoutingKey_MarksDestinationAnonymous` | Empty RoutingKey → `DisplayName == "anonymous publish"`, anonymous tag set |
| `Publish_EnricherThrows_RecordsEnrichmentException` | `Options.EnrichWithMessage = (_, _) => throw new(...)` → activity has `enrichment.exception` tag |

**Consume** (3):
| Test | Verifies |
|---|---|
| `Consume_ExtractsParentContext_FromTraceparentHeader` | Inject valid W3C `traceparent` (as byte[] per RabbitMQ convention) → activity parent matches |
| `Consume_SetsMessagingTags_AndDestinationFromHeader` | `DestinationAddress` header → destination tag + DisplayName |
| `Consume_WithoutDestinationHeader_MarksAnonymous` | No destination → anonymous tag + "anonymous receive" DisplayName |

**Send** (2):
| Test | Verifies |
|---|---|
| `Send_WithEndpoint_SetsNamedDestination` | Endpoint="svc.queue" → destination tag + "svc.queue publish" DisplayName |
| `Send_WithoutEndpoint_MarksAnonymous` | Empty endpoint → anonymous tag + "anonymous publish" DisplayName |

**TryGetExistingContext** (3):
| Test | Verifies |
|---|---|
| `TryGetExistingContext_WithTraceparent_ReturnsTrue_AndParsesContext` | Valid traceparent → returns true, context has correct TraceId |
| `TryGetExistingContext_WithNullHeaders_ReturnsFalse` | Null input → returns false, default context |
| `TryGetExistingContext_WithoutTraceHeaders_ReturnsFalse` | Headers without trace keys → returns false |

**Reset hook:** each Consume/Publish test that uses `ServiceConnectActivitySource.Options.EnrichWith*` resets it in `Dispose()` to avoid cross-test state leakage.

## Testing Strategy Notes

- **No test-double explosion.** Mock only what's strictly needed. `IProducer` is the only mock-heavy collaborator (in MessageBusWriteStream tests).
- **Deterministic.** No sleeps, timers, file watchers, threads, or network. GUID generation is acceptable since we only assert non-emptiness on SequenceId.
- **Assertion style matches existing tests.** Use `Assert.Equal`, `Assert.Contains`, `Assert.Throws`, `Assert.True/False`. Use `Mock.Verify(..., Times.Once())` where call count matters.
- **File-scoped helper types.** Where a test class needs a `Message` subclass or POCO (e.g., `DefaultProcessManagerPropertyMapperTests`), declare them as `file class` at the bottom of the test file — same pattern used by `AggregatorRegistryTests` and `StreamHandlerRegistryTests`.

## Commit Strategy

One commit per test file + one closeout commit:

1. `test: add MessageBusWriteStream unit tests (R-028)`
2. `test: add DefaultProcessManagerPropertyMapper unit tests (R-028)`
3. `test: add HeaderHelpers unit tests (R-028)`
4. `test: add MongoClientFactory unit tests (R-028)`
5. `test: add ServiceConnectActivitySource unit tests (R-028)`
6. `docs: mark R-028 done (unit test gap-fill, Group C-5)`

Each commit must leave the solution green (`dotnet build` + `dotnet test src/ServiceConnect.UnitTests`).

## Doc Update

In [docs/remaining-issues.md](../../remaining-issues.md):

- Update header paragraph: add "R-028 (unit test gap-fill) completed in Group C-5 on 2026-04-13."
- Flip R-028 row to **Done** with a note: "Every logic-bearing non-integration-heavy class now has unit tests. Remaining untested files are (a) broker/DB integration classes covered by E2E or (b) logic-less POCOs."

## Success Criteria

1. All 5 new test files compile and pass on `net8.0` and `net10.0`.
2. `dotnet test src/ServiceConnect.UnitTests` reports 323/323 passing (290 previous + ~33 new).
3. E2E suite stays green (73/73) — no production-code changes so this is a smoke check only.
4. Zero build warnings.
5. [docs/remaining-issues.md](../../remaining-issues.md) updated and R-028 marked Done.
