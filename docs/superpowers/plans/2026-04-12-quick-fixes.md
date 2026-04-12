# Quick Fixes (B-02, T-01, T-02) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix three verified issues: mutable ClientSettings exposure (B-02), disabled SSL certificate revocation (T-01), and inconsistent persistence exception types (T-02).

**Architecture:** Three independent commits. B-02 is the largest — changes `ITransportConfiguration.ClientSettings` from `IDictionary<string, object>` to `IReadOnlyDictionary<string, object>` with a new `SetClientSetting` method, then updates ~190 call sites across 42 files. T-01 and T-02 are one-file changes each.

**Tech Stack:** C# / .NET 8+, xUnit, Moq

**Design spec:** `docs/superpowers/specs/2026-04-12-quick-fixes-design.md`

---

### Task 1: Immutable ClientSettings — Interface, Implementation, and Unit Tests (B-02)

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Configuration/ITransportConfiguration.cs:25`
- Modify: `src/ServiceConnect/Configuration/TransportConfiguration.cs:37`
- Modify: `src/ServiceConnect.UnitTests/TransportConfigurationTests.cs:157-167`

- [ ] **Step 1: Write failing unit tests for new SetClientSetting method and read-only ClientSettings**

In `src/ServiceConnect.UnitTests/TransportConfigurationTests.cs`, replace the existing `ClientSettingsCanStoreValues` test (lines 157-167) with two new tests:

```csharp
[Fact]
public void SetClientSetting_StoresValues()
{
    var config = new TransportConfiguration();
    config.SetClientSetting("key1", "value1");
    config.SetClientSetting("key2", 42);

    Assert.Equal(2, config.ClientSettings.Count);
    Assert.Equal("value1", config.ClientSettings["key1"]);
    Assert.Equal(42, config.ClientSettings["key2"]);
}

[Fact]
public void SetClientSetting_OverwritesExistingValue()
{
    var config = new TransportConfiguration();
    config.SetClientSetting("key", "original");
    config.SetClientSetting("key", "updated");

    Assert.Single(config.ClientSettings);
    Assert.Equal("updated", config.ClientSettings["key"]);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "SetClientSetting" --no-restore`
Expected: FAIL — `TransportConfiguration` does not have a `SetClientSetting` method.

- [ ] **Step 3: Modify the interface**

In `src/ServiceConnect.Interfaces/Configuration/ITransportConfiguration.cs`, replace line 25:

```csharp
IDictionary<string, object> ClientSettings { get; set; }
```

with:

```csharp
IReadOnlyDictionary<string, object> ClientSettings { get; }
void SetClientSetting(string key, object value);
```

Add `using System.Collections.ObjectModel;` is NOT needed — `IReadOnlyDictionary` is in `System.Collections.Generic`.

- [ ] **Step 4: Modify the implementation**

In `src/ServiceConnect/Configuration/TransportConfiguration.cs`, replace line 37:

```csharp
public IDictionary<string, object> ClientSettings { get; set; } = new Dictionary<string, object>();
```

with:

```csharp
private readonly Dictionary<string, object> _clientSettings = new();
public IReadOnlyDictionary<string, object> ClientSettings => _clientSettings;

public void SetClientSetting(string key, object value)
{
    _clientSettings[key] = value;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "SetClientSetting" --no-restore`
Expected: PASS — both new tests green.

- [ ] **Step 6: Build unit test project to verify compilation**

Run: `dotnet build src/ServiceConnect.UnitTests`
Expected: 0 errors. The unit test project and its dependencies should compile cleanly.

Do NOT build the full solution yet — E2E tests won't compile until Task 2 updates the callers. Do NOT commit yet.

---

### Task 2: Update All ClientSettings Callers (B-02)

**Files:**
- Modify: 41 files in `src/ServiceConnect.EndToEndTests/` that use `t.ClientSettings["key"] = value`

All E2E test files follow the same pattern. Each `t.ClientSettings["key"] = value` must become `t.SetClientSetting("key", value)`.

- [ ] **Step 1: Bulk-replace all ClientSettings indexer writes**

Use sed to replace across all E2E test files. The pattern to replace:

```
t.ClientSettings["Port"] = _fixture.RabbitMqPort;
```
becomes:
```
t.SetClientSetting("Port", _fixture.RabbitMqPort);
```

The general regex: replace `t\.ClientSettings\["([^"]+)"\] = (.+);` with `t.SetClientSetting("$1", $2);`

Also handle the `QueueMappingTests.cs` which has multiple assignments on a single line separated by `;`:
```
t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
```
becomes:
```
t.SetClientSetting("RetryCount", 3); t.SetClientSetting("RetrySeconds", 1);
```

Also handle `PriorityQueueTests.cs` which stores a nested dictionary:
```
t.ClientSettings["Arguments"] = new Dictionary<string, object> { { "x-max-priority", 10 } };
```
becomes:
```
t.SetClientSetting("Arguments", new Dictionary<string, object> { { "x-max-priority", 10 } });
```

And `PublisherConfirmsTests.cs`:
```
t.ClientSettings["PublisherAcknowledgements"] = true;
```
becomes:
```
t.SetClientSetting("PublisherAcknowledgements", true);
```

Also handle `config.ClientSettings["key1"] = "value1"` pattern in `TransportConfigurationTests.cs` — but this test was already replaced in Task 1 Step 1. Verify that no indexer-write patterns remain in unit tests.

- [ ] **Step 2: Build the solution**

Run: `dotnet build`
Expected: 0 errors, 0 warnings. If any `ClientSettings[...] = ` patterns were missed, you'll get `CS0021` errors (cannot apply indexing to IReadOnlyDictionary).

- [ ] **Step 3: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests`
Expected: All pass (211+ tests).

- [ ] **Step 4: Run E2E tests**

Run: `sg docker dotnet test src/ServiceConnect.EndToEndTests`
Expected: All pass (71+ tests).

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Interfaces/Configuration/ITransportConfiguration.cs \
        src/ServiceConnect/Configuration/TransportConfiguration.cs \
        src/ServiceConnect.UnitTests/TransportConfigurationTests.cs \
        src/ServiceConnect.EndToEndTests/
git commit -m "fix: expose ClientSettings as IReadOnlyDictionary to prevent post-config mutation (B-02)"
```

---

### Task 3: Enable SSL Certificate Revocation Check (T-01)

**Files:**
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorMongoDbSsl.cs:122`

- [ ] **Step 1: Change CheckCertificateRevocation to true**

In `MessageDeduplicationPersistorMongoDbSsl.cs`, line 122, change:

```csharp
CheckCertificateRevocation = false
```

to:

```csharp
CheckCertificateRevocation = true
```

- [ ] **Step 2: Build the filters project**

Run: `dotnet build filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorMongoDbSsl.cs
git commit -m "fix: enable SSL certificate revocation check in MongoDbSsl dedup persistor (T-01)"
```

---

### Task 4: Consistent Persistence Exceptions in InMemoryProcessManagerFinder (T-02)

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs:133,214`
- Modify: `src/ServiceConnect.UnitTests/InMemoryProcessManagerFinderTests.cs:54,162`

- [ ] **Step 1: Update existing tests to expect PersistenceException instead of ArgumentException**

In `src/ServiceConnect.UnitTests/InMemoryProcessManagerFinderTests.cs`:

Line 54 — change:
```csharp
Assert.Throws<ArgumentException>(() => processManagerFinder.InsertData(dataWithDuplicateId));
```
to:
```csharp
Assert.Throws<PersistenceException>(() => processManagerFinder.InsertData(dataWithDuplicateId));
```

Line 162 — change:
```csharp
Assert.Throws<ArgumentException>(() => finder.InsertTimeout(MakeTimeoutData(id, DateTime.UtcNow.AddMinutes(10))));
```
to:
```csharp
Assert.Throws<PersistenceException>(() => finder.InsertTimeout(MakeTimeoutData(id, DateTime.UtcNow.AddMinutes(10))));
```

The `using ServiceConnect.Interfaces.Exceptions;` import is already present (line 4).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "ShouldThrowWhenInsertingDataWithExistingId|InsertTimeout_ThrowsWhenDuplicateId"`
Expected: Both tests FAIL — they throw `ArgumentException` but tests now expect `PersistenceException`.

- [ ] **Step 3: Change exception types in the implementation**

In `src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs`:

Line 133 — change:
```csharp
throw new ArgumentException($"ProcessManagerData with CorrelationId {key} already exists in the cache.");
```
to:
```csharp
throw new PersistenceException($"ProcessManagerData with CorrelationId {key} already exists in the cache.");
```

Line 214 — change:
```csharp
throw new ArgumentException($"TimeoutData with Id {key} already exists in the cache.");
```
to:
```csharp
throw new PersistenceException($"TimeoutData with Id {key} already exists in the cache.");
```

The `using ServiceConnect.Interfaces.Exceptions;` import is already present (line 4).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "ShouldThrowWhenInsertingDataWithExistingId|InsertTimeout_ThrowsWhenDuplicateId"`
Expected: Both tests PASS.

- [ ] **Step 5: Run full unit test suite**

Run: `dotnet test src/ServiceConnect.UnitTests`
Expected: All pass (211+ tests).

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs \
        src/ServiceConnect.UnitTests/InMemoryProcessManagerFinderTests.cs
git commit -m "fix: use PersistenceException for duplicate-key errors in InMemoryProcessManagerFinder (T-02)"
```
