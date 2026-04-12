# Dedup Filter Restructuring (R-022, R-027) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collapse the 8 boilerplate filter variant classes into 2 generic classes with a persistor factory, consolidate MongoDb/MongoDbSsl persistors into one using driver-native parsing, and remove Redis support.

**Architecture:** A `PersistorType` enum + `PersistorFactory` replaces the type-per-backend pattern. The MongoDb persistor gains optional SSL certificate support via `MongoUrl`/`MongoClientSettings.FromUrl()` instead of manual string parsing. Redis classes, package dependency, and settings are removed entirely.

**Tech Stack:** C# / .NET 10, xUnit, Moq, MongoDB.Driver 2.4.4

**Design spec:** `docs/superpowers/specs/2026-04-13-dedup-filter-restructuring-design.md`

**Base path:** All filter project paths are relative to `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/` (referred to as `$F` below for orientation, but all steps use full paths).

---

### Task 1: PersistorType Enum + PersistorFactory + Tests (TDD)

**Files:**
- Create: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/PersistorType.cs`
- Create: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/PersistorFactory.cs`
- Create: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/PersistorFactoryTests.cs`

- [ ] **Step 1: Write failing tests for PersistorFactory**

Create `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/PersistorFactoryTests.cs`:

```csharp
using System;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using Xunit;

namespace ServiceConnect.Filters.MessageDeduplication.Tests
{
    public class PersistorFactoryTests
    {
        [Fact]
        public void Create_InMemory_ReturnsInMemoryPersistor()
        {
            var persistor = PersistorFactory.Create(PersistorType.InMemory);
            Assert.IsType<MessageDeduplicationPersistorInMemory>(persistor);
        }

        [Fact]
        [Trait("Category", "Docker")]
        public void Create_MongoDb_ReturnsMongoDbPersistor()
        {
            var persistor = PersistorFactory.Create(PersistorType.MongoDb);
            Assert.IsType<MessageDeduplicationPersistorMongoDb>(persistor);
        }

        [Fact]
        public void Create_InvalidType_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PersistorFactory.Create((PersistorType)999));
        }
    }
}
```

Note: The MongoDb test has `[Trait("Category", "Docker")]` because the `MessageDeduplicationPersistorMongoDb` constructor connects to MongoDB. When running without Docker infrastructure, filter it out.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests --filter "PersistorFactory" --no-restore`
Expected: FAIL — `PersistorType` and `PersistorFactory` do not exist.

- [ ] **Step 3: Create PersistorType enum**

Create `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/PersistorType.cs`:

```csharp
namespace ServiceConnect.Filters.MessageDeduplication
{
    public enum PersistorType
    {
        InMemory,
        MongoDb
    }
}
```

- [ ] **Step 4: Create PersistorFactory**

Create `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/PersistorFactory.cs`:

```csharp
using System;
using ServiceConnect.Filters.MessageDeduplication.Persistors;

namespace ServiceConnect.Filters.MessageDeduplication
{
    public static class PersistorFactory
    {
        public static IMessageDeduplicationPersistor Create(PersistorType type) => type switch
        {
            PersistorType.InMemory => new MessageDeduplicationPersistorInMemory(),
            PersistorType.MongoDb => new MessageDeduplicationPersistorMongoDb(),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported persistor type.")
        };
    }
}
```

- [ ] **Step 5: Run non-Docker tests to verify they pass**

Run: `dotnet test filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests --filter "PersistorFactory&Category!=Docker"`
Expected: 2 tests PASS (`Create_InMemory_ReturnsInMemoryPersistor`, `Create_InvalidType_ThrowsArgumentOutOfRangeException`).

- [ ] **Step 6: Build the full filter solution to verify compilation**

Run: `dotnet build filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication`
Expected: 0 errors.

- [ ] **Step 7: Commit**

```bash
git add filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/PersistorType.cs \
      filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/PersistorFactory.cs \
      filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/PersistorFactoryTests.cs
git commit -m "feat: add PersistorType enum and PersistorFactory for dedup filters (R-022)"
```

---

### Task 2: Settings Cleanup + MongoDb Persistor Consolidation + MongoDbSsl Removal (R-027)

**Files:**
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationFilterSettings.cs`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorMongoDb.cs`
- Delete: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorMongoDbSsl.cs`
- Delete: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilterMongoDbSsl.cs`
- Delete: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilterMongoDbSsl.cs`

- [ ] **Step 1: Add new properties and PersistorType to DeduplicationFilterSettings**

In `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationFilterSettings.cs`, add the following properties after the existing `CollectionNameMongoDb` property (after line 39):

```csharp
/// <summary>
/// Path to X509 certificate file for MongoDB SSL client authentication.
/// If set, TLS is auto-enabled on the connection.
/// Takes precedence over MongoDbCertBase64 if both are set.
/// </summary>
public string MongoDbCertPath { get; set; }

/// <summary>
/// Base64-encoded X509 certificate for MongoDB SSL client authentication.
/// Alternative to MongoDbCertPath for environments where file paths are impractical.
/// </summary>
public string MongoDbCertBase64 { get; set; }

/// <summary>
/// Password for the X509 certificate (optional).
/// Used with both MongoDbCertPath and MongoDbCertBase64.
/// </summary>
public string MongoDbCertPassphrase { get; set; }

/// <summary>
/// Which persistor backend to use for message deduplication.
/// </summary>
public PersistorType PersistorType { get; set; }
```

In the private constructor (line 70-80), add the default for `PersistorType` after the existing defaults:

```csharp
PersistorType = PersistorType.InMemory;
```

The cert properties default to `null` (reference type default), so no explicit initialization needed.

- [ ] **Step 2: Rewrite MessageDeduplicationPersistorMongoDb with SSL support**

Replace the entire contents of `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorMongoDb.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using Common.Logging;
using MongoDB.Driver;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors
{
    public class MessageDeduplicationPersistorMongoDb : IMessageDeduplicationPersistor
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(MessageDeduplicationPersistorMongoDb));
        private readonly IMongoCollection<ProcessedMessage> _collection;

        public MessageDeduplicationPersistorMongoDb()
        {
            var filterSettings = DeduplicationFilterSettings.Instance;

            var url = new MongoUrl(filterSettings.ConnectionStringMongoDb);
            var clientSettings = MongoClientSettings.FromUrl(url);

            if (!string.IsNullOrEmpty(filterSettings.MongoDbCertPath) ||
                !string.IsNullOrEmpty(filterSettings.MongoDbCertBase64))
            {
                X509Certificate2 cert;
                if (!string.IsNullOrEmpty(filterSettings.MongoDbCertPath))
                {
                    cert = string.IsNullOrEmpty(filterSettings.MongoDbCertPassphrase)
                        ? new X509Certificate2(filterSettings.MongoDbCertPath)
                        : new X509Certificate2(filterSettings.MongoDbCertPath, filterSettings.MongoDbCertPassphrase);
                }
                else
                {
                    var certBytes = Convert.FromBase64String(filterSettings.MongoDbCertBase64);
                    cert = string.IsNullOrEmpty(filterSettings.MongoDbCertPassphrase)
                        ? new X509Certificate2(certBytes)
                        : new X509Certificate2(certBytes, filterSettings.MongoDbCertPassphrase);
                }

                clientSettings.UseSsl = true;
                clientSettings.SslSettings = new SslSettings
                {
                    ClientCertificates = new List<X509Certificate> { cert },
                    ClientCertificateSelectionCallback = (sender, host, certificates, certificate, issuers) => certificates[0],
                    CheckCertificateRevocation = true
                };
            }

            var mongoClient = new MongoClient(clientSettings);
            var mongoDatabase = mongoClient.GetDatabase(filterSettings.DatabaseNameMongoDb);
            _collection = mongoDatabase.GetCollection<ProcessedMessage>(filterSettings.CollectionNameMongoDb);
            _collection.Indexes.CreateOneAsync(Builders<ProcessedMessage>.IndexKeys.Ascending(_ => _.Id));
            _collection.Indexes.CreateOneAsync(Builders<ProcessedMessage>.IndexKeys.Ascending(_ => _.ExpiryDateTime));
        }

        public bool GetMessageExists(Guid messageId)
        {
            IAsyncCursor<ProcessedMessage> result = _collection.FindAsync(i => i.Id == messageId).Result;
            return result.Any();
        }

        public void Insert(Guid messageId, DateTime messagExpiry)
        {
            try
            {
                _collection.InsertOne(new ProcessedMessage
                {
                    Id = messageId,
                    ExpiryDateTime = messagExpiry
                });
            }
            catch (Exception ex)
            {
                Logger.Fatal("Error inserting into ProcessedMessage collection", ex);
            }
        }

        public void RemoveExpiredMessages(DateTime messagExpiry)
        {
            try
            {
                _collection.DeleteMany(i => i.ExpiryDateTime < messagExpiry);
            }
            catch (Exception ex)
            {
                Logger.Fatal("Error cleaning up expired ProcessedMessages", ex);
            }
        }
    }
}
```

- [ ] **Step 3: Delete MongoDbSsl persistor and its filter variants**

Delete these 3 files:
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorMongoDbSsl.cs`
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilterMongoDbSsl.cs`
- `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilterMongoDbSsl.cs`

```bash
git rm filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorMongoDbSsl.cs \
      filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilterMongoDbSsl.cs \
      filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilterMongoDbSsl.cs
```

- [ ] **Step 4: Build to verify compilation**

Run: `dotnet build filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication`
Expected: 0 errors. The MongoDbSsl references are gone. The remaining filter variants (InMemory, MongoDb, Redis) still compile.

- [ ] **Step 5: Run existing tests**

Run: `dotnet test filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests --filter "Category!=Docker"`
Expected: All non-Docker tests pass (IncomingFilterTests: 4, OutgoingFilterTests: 3, PersistorFactoryTests: 2 = 9 total). Note: the Redis timer test still exists at this point and passes without Redis because OutgoingFilter swallows exceptions.

- [ ] **Step 6: Commit**

```bash
git add filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationFilterSettings.cs \
      filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorMongoDb.cs
git commit -m "fix: consolidate MongoDb/MongoDbSsl persistors with driver-native SSL support (R-027)"
```

---

### Task 3: Redis Removal + OutgoingFilter Cleanup

**Files:**
- Delete: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorRedis.cs`
- Delete: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilterRedis.cs`
- Delete: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilterRedis.cs`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationFilterSettings.cs`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingFilter.cs:42`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.csproj`
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/OutgoingFilterTests.cs`

- [ ] **Step 1: Delete Redis persistor and filter variants**

```bash
git rm filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Persistors/MessageDeduplicationPersistorRedis.cs \
      filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilterRedis.cs \
      filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilterRedis.cs
```

- [ ] **Step 2: Remove Redis settings properties from DeduplicationFilterSettings**

In `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationFilterSettings.cs`:

Remove the `ConnectionStringRedis` property and its XML doc comment:

```csharp
/// <summary>
/// Redis persistance store connection string
/// </summary>
public string ConnectionStringRedis { get; set; }
```

Remove the `DatabaseIndexRedis` property and its XML doc comment:

```csharp
/// <summary>
/// Database index (0-15)
/// </summary>
public int DatabaseIndexRedis { get; set; }
```

Remove their defaults from the constructor:

```csharp
ConnectionStringRedis = "localhost,abortConnect=false";
DatabaseIndexRedis = 0;
```

- [ ] **Step 3: Remove StackExchange.Redis from csproj**

In `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.csproj`, remove this line:

```xml
<PackageReference Include="StackExchange.Redis" Version="1.2.6" />
```

- [ ] **Step 4: Simplify OutgoingFilter timer check**

In `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingFilter.cs`, line 42, replace:

```csharp
if (_timer == null && !_settings.DisableMsgExpiry && messageDeduplicationPersistor.GetType() != typeof(MessageDeduplicationPersistorRedis))
```

with:

```csharp
if (_timer == null && !_settings.DisableMsgExpiry)
```

Also remove the `using` for the Redis persistor if present. The existing `using ServiceConnect.Filters.MessageDeduplication.Persistors;` import covers all persistors — check if there's a specific Redis import to remove (there isn't in the current code, the general Persistors namespace import is sufficient).

- [ ] **Step 5: Delete Redis timer test from OutgoingFilterTests**

In `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/OutgoingFilterTests.cs`, delete the entire `ShouldNotSetTimerWhenUsingRedisPersistor` test method (lines 72-95):

```csharp
[Fact]
public void ShouldNotSetTimerWhenUsingRedisPersistor()
{
    // Arrange
    Guid messageId = Guid.NewGuid();

    var deduplicationSettings = DeduplicationFilterSettings.Instance;
    deduplicationSettings.DisableMsgExpiry = false;

    IMessageDeduplicationPersistor persistor = new MessageDeduplicationPersistorRedis();

    var outgoingFilter = new OutgoingFilter(persistor);
    var envelope = new Envelope();
    envelope.Headers = new Dictionary<string, object>();
    envelope.Headers = new Dictionary<string, object> { { "MessageId", Encoding.ASCII.GetBytes(messageId.ToString()) } };


    // Act
    var result = outgoingFilter.Process(envelope);


    // Assert
    Assert.Null(outgoingFilter.Timer);
}
```

- [ ] **Step 6: Build to verify compilation**

Run: `dotnet build filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication`
Expected: 0 errors. All Redis references are gone.

- [ ] **Step 7: Run tests**

Run: `dotnet test filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests --filter "Category!=Docker"`
Expected: All pass. The Redis timer test is gone. Remaining: IncomingFilterTests (4), OutgoingFilterTests (2), PersistorFactoryTests (2) = 8 total.

- [ ] **Step 8: Commit**

```bash
git add filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/DeduplicationFilterSettings.cs \
      filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingFilter.cs \
      filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.csproj \
      filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests/OutgoingFilterTests.cs
git commit -m "refactor: remove Redis dedup support and simplify OutgoingFilter timer logic"
```

---

### Task 4: Filter Collapse — Delete Remaining Variants + Create Generic Filters (R-022)

**Files:**
- Delete: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilterInMemory.cs`
- Delete: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilterMongoDb.cs`
- Delete: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilterInMemory.cs`
- Delete: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilterMongoDb.cs`
- Create: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilter.cs`
- Create: `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilter.cs`

- [ ] **Step 1: Delete remaining 4 filter variant classes**

```bash
git rm filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilterInMemory.cs \
      filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilterMongoDb.cs \
      filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilterInMemory.cs \
      filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilterMongoDb.cs
```

- [ ] **Step 2: Create IncomingDeduplicationFilter**

Create `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilter.cs`:

```csharp
using System;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class IncomingDeduplicationFilter : IFilter
    {
        private static readonly Lazy<IncomingFilter> _incomingFilter = new(() =>
            new IncomingFilter(PersistorFactory.Create(DeduplicationFilterSettings.Instance.PersistorType)));

        public IBus Bus { get; set; }

        public bool Process(Envelope envelope)
        {
            return _incomingFilter.Value.Process(envelope);
        }
    }
}
```

- [ ] **Step 3: Create OutgoingDeduplicationFilter**

Create `filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilter.cs`:

```csharp
using System;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class OutgoingDeduplicationFilter : IFilter
    {
        private static readonly Lazy<OutgoingFilter> _outgoingFilter = new(() =>
            new OutgoingFilter(PersistorFactory.Create(DeduplicationFilterSettings.Instance.PersistorType)));

        public IBus Bus { get; set; }

        public bool Process(Envelope envelope)
        {
            return _outgoingFilter.Value.Process(envelope);
        }
    }
}
```

- [ ] **Step 4: Build to verify compilation**

Run: `dotnet build filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication`
Expected: 0 errors. All 8 old filter variants are gone, replaced by 2 new generic classes.

- [ ] **Step 5: Run all tests**

Run: `dotnet test filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.Tests --filter "Category!=Docker"`
Expected: All 8 non-Docker tests pass.

- [ ] **Step 6: Commit**

```bash
git add filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/IncomingDeduplicationFilter.cs \
      filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication/Filters/OutgoingDeduplicationFilter.cs
git commit -m "refactor: collapse 8 filter variants into 2 generic classes with PersistorFactory (R-022)"
```

---

### Task 5: Update Deferred Doc + Run E2E Tests

**Files:**
- Modify: `docs/remaining-issues.md`

- [ ] **Step 1: Update remaining-issues.md**

In `docs/remaining-issues.md`, update R-022 and R-027 entries to mark them as done:

Change R-022:
```
| R-022 | Architecture | Dedup filter combinatorial explosion | Large — Group B: collapse 8 filter variants to 2, add PersistorFactory + PersistorType enum, remove Redis support entirely |
```
to:
```
| R-022 | Architecture | Dedup filter combinatorial explosion | **Done** (Group B) — collapsed 8 filter variants to 2 + PersistorFactory, removed Redis support |
```

Change R-027:
```
| R-027 | Tech Debt | MongoDbSsl manual connection string parsing | Large — Group B: merge MongoDbSsl into MongoDb persistor, use driver-native MongoUrl parsing, add cert settings properties |
```
to:
```
| R-027 | Tech Debt | MongoDbSsl manual connection string parsing | **Done** (Group B) — merged MongoDbSsl into MongoDb persistor with driver-native MongoUrl parsing |
```

- [ ] **Step 2: Commit the doc update**

```bash
git add docs/remaining-issues.md
git commit -m "docs: mark R-022 and R-027 as done in remaining issues tracker"
```

- [ ] **Step 3: Run E2E tests in background**

Run: `sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"`

Run this in the background and monitor output. Expected: All 71+ E2E tests pass. The E2E tests use a custom `TestDeduplicationFilter` and do not reference the filter library's variant types, so they are unaffected by this restructuring.

---
