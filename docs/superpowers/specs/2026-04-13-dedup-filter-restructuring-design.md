# Dedup Filter Restructuring (R-022, R-027) -- Design Spec

**Date:** 2026-04-13
**Scope:** R-022 (combinatorial explosion), R-027 (MongoDbSsl manual parsing) from remaining issues tracker
**Breaking changes:** Yes -- filter type names removed, Redis support removed, MongoDbSsl custom connection string format removed

## Problem

The `ServiceConnect.Filters.MessageDeduplication` project has two structural issues:

1. **R-022 (Combinatorial explosion):** 8 filter variant classes exist (2 directions x 4 persistor backends). Each is ~15 lines of boilerplate that differs only in which `IMessageDeduplicationPersistor` it instantiates inside a `Lazy<>`. The base logic lives in `IncomingFilter` and `OutgoingFilter` -- the variant classes add nothing.

2. **R-027 (MongoDbSsl manual parsing):** `MessageDeduplicationPersistorMongoDbSsl` manually parses a custom connection string format (`nodes=host1;host2,username=admin,certpath=/path`) using `string.Split()` with no error handling, no bounds checking, and no support for values containing `=` or `,`. The MongoDB driver provides `MongoUrl` and `MongoClientSettings.FromUrl()` for this purpose.

Additionally, Redis support is being removed entirely -- it adds a dependency (`StackExchange.Redis`), dedicated classes, and settings properties for a backend that can be reintroduced later if needed.

## Design

### Persistor Consolidation (R-027)

**Merge `MessageDeduplicationPersistorMongoDb` and `MessageDeduplicationPersistorMongoDbSsl` into a single `MessageDeduplicationPersistorMongoDb`.**

The two classes share identical domain logic (`GetMessageExists`, `Insert`, `RemoveExpiredMessages`). The only difference is connection setup.

The consolidated persistor:
1. Parses `ConnectionStringMongoDb` using `new MongoUrl(connectionString)` (driver-native)
2. Converts to `MongoClientSettings` via `MongoClientSettings.FromUrl(url)`
3. If `MongoDbCertPath` or `MongoDbCertBase64` is set in settings, overlays `SslSettings` with client certificates onto the `MongoClientSettings`
4. Creates `MongoClient` with the resulting settings

SSL transport encryption (without client certs) is handled by the standard connection string (`?tls=true` or `?ssl=true`). Client certificate authentication is handled by the new settings properties.

**New settings properties on `DeduplicationFilterSettings`:**

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| `MongoDbCertPath` | `string` | `null` | Path to X509 certificate file |
| `MongoDbCertBase64` | `string` | `null` | Base64-encoded certificate (alternative to file path) |
| `MongoDbCertPassphrase` | `string` | `null` | Certificate password (optional) |

SSL client certs are enabled implicitly when either `MongoDbCertPath` or `MongoDbCertBase64` is set. No separate `SslEnabled` flag needed. If both are set, `MongoDbCertPath` takes precedence. When client certs are provided, TLS is auto-enabled on the `MongoClientSettings` (sets `UseSsl = true`) even if the connection string doesn't include `?tls=true`.

**Deleted:** `MessageDeduplicationPersistorMongoDbSsl` class.

### Filter Collapse (R-022)

**Replace all 8 filter variant classes with 2 generic classes + a factory.**

**New `PersistorType` enum:**
```csharp
public enum PersistorType
{
    InMemory,
    MongoDb
}
```

Added as a property on `DeduplicationFilterSettings` with default `InMemory`.

**New `PersistorFactory` static class:**
```csharp
public static class PersistorFactory
{
    public static IMessageDeduplicationPersistor Create(PersistorType type) => type switch
    {
        PersistorType.InMemory => new MessageDeduplicationPersistorInMemory(),
        PersistorType.MongoDb => new MessageDeduplicationPersistorMongoDb(),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
```

Testable by passing the enum directly -- no need to mock the settings singleton.

**New filter classes:**
```csharp
public class IncomingDeduplicationFilter : IFilter
{
    private static readonly Lazy<IncomingFilter> _incomingFilter = new(() =>
        new IncomingFilter(PersistorFactory.Create(DeduplicationFilterSettings.Instance.PersistorType)));

    public bool Process(Envelope envelope) => _incomingFilter.Value.Process(envelope);
    public IBus Bus { get; set; }
}
```

`OutgoingDeduplicationFilter` follows the same pattern.

**Deleted (8 classes):**
- `IncomingDeduplicationFilterInMemory`
- `IncomingDeduplicationFilterMongoDb`
- `IncomingDeduplicationFilterMongoDbSsl`
- `IncomingDeduplicationFilterRedis`
- `OutgoingDeduplicationFilterInMemory`
- `OutgoingDeduplicationFilterMongoDb`
- `OutgoingDeduplicationFilterMongoDbSsl`
- `OutgoingDeduplicationFilterRedis`

### Redis Removal

**Deleted:**
- `MessageDeduplicationPersistorRedis` class (includes `RedisConnectionFactory` in same file)
- `StackExchange.Redis` package reference from csproj
- `ConnectionStringRedis` and `DatabaseIndexRedis` properties from `DeduplicationFilterSettings`

**Modified:** `OutgoingFilter` constructor -- remove the `typeof(MessageDeduplicationPersistorRedis)` type check for timer setup. With only InMemory and MongoDb remaining, both need the cleanup timer, so the check simplifies to:
```csharp
if (_timer == null && !_settings.DisableMsgExpiry)
```

**Test deleted:** `ShouldNotSetTimerWhenUsingRedisPersistor` -- no longer applicable.

### Settings Changes Summary

**`DeduplicationFilterSettings` after restructuring:**

| Property | Status | Default |
|----------|--------|---------|
| `PersistorType` | **New** | `PersistorType.InMemory` |
| `MongoDbCertPath` | **New** | `null` |
| `MongoDbCertBase64` | **New** | `null` |
| `MongoDbCertPassphrase` | **New** | `null` |
| `MsgExpiryHours` | Unchanged | `24` |
| `MsgCleanupIntervalMinutes` | Unchanged | `60` |
| `DisableMsgExpiry` | Unchanged | `false` |
| `ConnectionStringMongoDb` | Unchanged | `"mongodb://localhost"` |
| `DatabaseNameMongoDb` | Unchanged | `"ServiceConnect-Filters-MessageDeduplication"` |
| `CollectionNameMongoDb` | Unchanged | `"ProcessedMessages"` |
| `ConnectionStringRedis` | **Deleted** | -- |
| `DatabaseIndexRedis` | **Deleted** | -- |

## Breaking Changes

| Change | Migration |
|--------|-----------|
| Filter type names removed (e.g., `IncomingDeduplicationFilterMongoDb`) | Use `IncomingDeduplicationFilter` + set `PersistorType` in settings |
| Redis support removed | Migrate to InMemory or MongoDb |
| MongoDbSsl custom connection string format removed | Use standard MongoDB URI + set cert properties in settings |
| `ConnectionStringRedis` / `DatabaseIndexRedis` settings removed | Delete references |

## Testing

- **New:** `PersistorFactory.Create()` returns correct type for each `PersistorType` enum value
- **New:** `PersistorFactory.Create()` throws `ArgumentOutOfRangeException` for invalid enum value
- **New:** MongoDb persistor builds `MongoClientSettings` with SSL certs when cert properties are set
- **Deleted:** `ShouldNotSetTimerWhenUsingRedisPersistor` (Redis removed)
- **Unchanged:** 4 `IncomingFilterTests` + 2 remaining `OutgoingFilterTests` -- these test `IncomingFilter`/`OutgoingFilter` with mock persistors, unaffected by restructuring

## File Changes Summary

| Action | File |
|--------|------|
| Delete | `Filters/IncomingDeduplicationFilterInMemory.cs` |
| Delete | `Filters/IncomingDeduplicationFilterMongoDb.cs` |
| Delete | `Filters/IncomingDeduplicationFilterMongoDbSsl.cs` |
| Delete | `Filters/IncomingDeduplicationFilterRedis.cs` |
| Delete | `Filters/OutgoingDeduplicationFilterInMemory.cs` |
| Delete | `Filters/OutgoingDeduplicationFilterMongoDb.cs` |
| Delete | `Filters/OutgoingDeduplicationFilterMongoDbSsl.cs` |
| Delete | `Filters/OutgoingDeduplicationFilterRedis.cs` |
| Delete | `Persistors/MessageDeduplicationPersistorMongoDbSsl.cs` |
| Delete | `Persistors/MessageDeduplicationPersistorRedis.cs` (includes `RedisConnectionFactory`) |
| Create | `Filters/IncomingDeduplicationFilter.cs` |
| Create | `Filters/OutgoingDeduplicationFilter.cs` |
| Create | `PersistorType.cs` |
| Create | `PersistorFactory.cs` |
| Modify | `Persistors/MessageDeduplicationPersistorMongoDb.cs` (add SSL support) |
| Modify | `DeduplicationFilterSettings.cs` (add/remove properties) |
| Modify | `Filters/OutgoingFilter.cs` (remove Redis type check) |
| Modify | `ServiceConnect.Filters.MessageDeduplication.csproj` (remove StackExchange.Redis) |
| Modify | Tests (add factory tests, delete Redis timer test) |

## Out of Scope

- R-032 (DeduplicationFilterSettings singleton) -- deferred to Group C; requires DI migration of core framework (R-009)
- R-017/R-018 (silent exception swallowing) -- deferred to Group C; needs IFilter interface change for async
- R-028 (zero unit test coverage) -- ongoing effort; this work adds factory tests but doesn't aim for comprehensive coverage
- Reintroducing Redis support -- can be added later as a new `PersistorType` value if needed
