# Quick Fixes (B-02, T-01, T-02) -- Design Spec

**Date:** 2026-04-12
**Scope:** B-02, T-01, T-02 from remaining issues tracker
**Breaking changes:** Yes -- B-02 changes `ITransportConfiguration.ClientSettings` from `IDictionary` to `IReadOnlyDictionary` and adds `SetClientSetting` method

## Problem

Three verified issues of varying severity remain after the async/threading fixes:

1. **B-02:** `ITransportConfiguration.ClientSettings` is typed as `IDictionary<string, object>` with a public setter. Callers can mutate or replace the dictionary after configuration, leading to unpredictable runtime behavior. The dictionary is set during `ConfigureTransport()` callbacks and read at runtime by `Client.cs`, `Connection.cs`, and `Producer.cs`.

2. **T-01:** `MessageDeduplicationPersistorMongoDbSsl` hardcodes `CheckCertificateRevocation = false` in its SSL settings (line 122). This disables OCSP revocation checking, meaning connections will succeed even if the server certificate has been revoked -- a security risk.

3. **T-02:** `InMemoryProcessManagerFinder` throws `ArgumentException` for persistence conflicts (duplicate CorrelationId on lines 133 and 214) where `PersistenceException` is the correct type. Other persistence errors in the same class (line 188) already use `PersistenceException`. The inconsistency makes it harder for callers to handle persistence errors uniformly.

## Design

### Commit 1: Immutable ClientSettings (B-02)

**Interface change (`ITransportConfiguration.cs`):**

Replace:
```csharp
IDictionary<string, object> ClientSettings { get; set; }
```

With:
```csharp
IReadOnlyDictionary<string, object> ClientSettings { get; }
void SetClientSetting(string key, object value);
```

**Implementation change (`TransportConfiguration.cs`):**

- Back `ClientSettings` with a private `Dictionary<string, object> _clientSettings`
- Expose as `IReadOnlyDictionary<string, object>` via getter
- Implement `SetClientSetting(string key, object value)` to add/update entries in `_clientSettings`

**Internal reader changes:**

All internal code reads via `ClientSettings.TryGetValue(...)` which exists on `IReadOnlyDictionary` -- no changes needed for:
- `Connection.cs` lines 12-13, 41 (TryGetValue calls)
- `Producer.cs` line 75 (TryGetValue call)
- `Client.cs` lines 44, 46-47 (TryGetValue calls)

One internal site needs fixing:
- `Client.cs` line 48: casts `ClientSettings` value to `IDictionary<string, object?>` for `_queueArguments`. The value stored under `RabbitMQSettingKeys.Arguments` is itself a `Dictionary<string, object>` set by callers. This cast remains valid since the stored value's runtime type is still `Dictionary`. No change needed.

**Caller changes (all in E2E tests and unit tests):**

Every `t.ClientSettings["key"] = value` becomes `t.SetClientSetting("key", value)`. This pattern appears ~190 times across ~40 E2E test files. The `TransportConfigurationTests.cs` unit test also needs updating.

**Test:**

Add a unit test verifying `ClientSettings` is read-only (cannot be assigned) and `SetClientSetting` works correctly.

### Commit 2: Enable certificate revocation check (T-01)

In `MessageDeduplicationPersistorMongoDbSsl.cs` line 122, change:
```csharp
CheckCertificateRevocation = false
```
to:
```csharp
CheckCertificateRevocation = true
```

One-line fix. No test changes -- this is in the dedup filter project which has no unit tests (R-028).

### Commit 3: Consistent persistence exceptions (T-02)

In `InMemoryProcessManagerFinder.cs`:

**Line 133** -- `InsertData` duplicate CorrelationId:
```csharp
// Before:
throw new ArgumentException($"ProcessManagerData with CorrelationId {key} already exists in the cache.");
// After:
throw new PersistenceException($"ProcessManagerData with CorrelationId {key} already exists in the cache.");
```

**Line 214** -- `InsertTimeout` duplicate Id:
```csharp
// Before:
throw new ArgumentException($"TimeoutData with Id {key} already exists in the cache.");
// After:
throw new PersistenceException($"TimeoutData with Id {key} already exists in the cache.");
```

Other exception sites remain unchanged:
- Line 36 `InvalidOperationException` (no mapping found) -- correct, this is a configuration state error
- Line 53 `ArgumentException` (null property value) -- correct, this is invalid input
- Line 75 `InvalidOperationException` (incompatible types) -- correct, this is a configuration error

**Test:**

Update or add unit tests verifying that `InsertData` and `InsertTimeout` throw `PersistenceException` (not `ArgumentException`) for duplicate entries.

## Commit Sequence

| # | Message | Scope | Breaking? |
|---|---------|-------|-----------|
| 1 | `fix: expose ClientSettings as IReadOnlyDictionary to prevent post-config mutation (B-02)` | ITransportConfiguration, TransportConfiguration, all E2E tests | Yes |
| 2 | `fix: enable SSL certificate revocation check in MongoDbSsl dedup persistor (T-01)` | MessageDeduplicationPersistorMongoDbSsl | No |
| 3 | `fix: use PersistenceException for duplicate-key errors in InMemoryProcessManagerFinder (T-02)` | InMemoryProcessManagerFinder | No (internal) |

## Out of Scope

- All Group B issues (dedup filter restructuring: R-017/018, R-022, R-027, R-032)
- All Group C issues (core architecture: R-009, R-020/021, R-034)
- Making `CheckCertificateRevocation` configurable (deferred to Group B with DeduplicationFilterSettings rework)
