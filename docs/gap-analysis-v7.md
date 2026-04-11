# Gap Analysis: ServiceConnect v6.x → v7.x

## Overview

This document captures functional features that existed in the master branch (v6.x) but may be missing or different in the current branch (v7.x refactor).

**Note:** This analysis excludes syntactic/breaking changes that are expected in a major version release (e.g., synchronous → async API, configuration API restructuring).

---

## Missing Features (Not Implemented)

### 1. Heartbeat / Monitoring System

| Component | Status |
|-----------|--------|
| `HeartbeatMessage` class | **Missing** |
| `HeartbeatTimerState` class | **Missing** |
| Code sends heartbeats | **Missing** |

**Impact:** No built-in heartbeat/monitoring for consumers.

---

### 2. Process Manager Timeout Handling

| Component | Status |
|-----------|--------|
| `ExpiredTimeoutsPoller` class | **Missing** |
| Timeout polling for process managers | **Missing** |

**Impact:** Process manager timeouts will not fire.

---

### 3. Middleware Pipeline Not Invoked

| Component | Status |
|-----------|--------|
| `MessageProcessingMiddleware` collection | ✅ Exists |
| `SendMessageMiddleware` collection | ✅ Exists |
| Interfaces defined (`ISendMessageMiddleware`) | ✅ Exists |
| Invoked anywhere | ❌ **NOT USED** |

**Impact:** Users cannot add custom middleware.

---

### 4. ExceptionHandler Not Used

| Component | Status |
|-----------|--------|
| Configuration property exists | ✅ Yes |
| Code calls it on exceptions | ❌ **NOT USED** |

**Impact:** No global exception handler callback.

---

### 5. Process Manager Async Interface Support

The codebase has `IProcessHandler<,>` but `ProcessManagerProcessor` doesn't handle legacy `IStartProcessManager` interface.

**Impact:** Legacy process manager patterns may not work.

---

## Configuration Properties Verified Working

### 6. PurgeQueueOnStartup

✅ **VERIFIED** - Used in RabbitMQ Consumer.cs:
```csharp
if (_queueConfiguration.PurgeQueueOnStartup)
{
    _ = _model.QueuePurge(queueName);
}
```

---

## Configuration Properties Not Used

### 7. AutoStartConsuming Not Working

- Config exists: ✅
- Code reads it: ❌ **NOT USED** - No auto-start logic in Bus

Users must explicitly call `StartConsumingAsync()`.

---

### 8. Clients Configuration Not Used

- Config exists: ✅
- Code reads it: ❌ **NOT USED** - No multi-consumer support

---

### 9. HeartbeatQueueName Not Used

- Config exists: ✅
- Code sends heartbeats: ❌ **NOT USED** - No heartbeat system

---

### 10. EnableProcessManagerTimeouts Not Used

- Config exists: ✅
- Code polls for timeouts: ❌ **NOT USED** - No timeout poller

---

## Intentionally Removed (Not Gaps)

| Feature | Notes |
|---------|-------|
| SQL Server persistor | Intentional removal |
| Legacy container packages (Ninject, StructureMap) | Intentional removal |
| MongoDB SSL variant | May need reconsideration |

---

## Configuration Options - Complete Usage Map

### IBusConfiguration

| Property | Status | Notes |
|----------|--------|-------|
| `ScanForMessageHandlers` | ✅ Used | In ServiceCollectionExtensions |
| `AutoStartConsuming` | ❌ Not Used | No auto-start logic |
| `EnableProcessManagerTimeouts` | ❌ Not Used | No timeout poller |
| `Clients` | ❌ Not Used | No multi-consumer support |
| `ExceptionHandler` | ❌ Not Used | Never called |
| `Transport` | ✅ Used | All properties used |
| `Queues` | ✅ Used | All properties used |
| `Persistence` | ✅ Used | All properties used |
| `Pipeline` | ⚠️ Partial | Filters used, middleware not invoked |

### ITransportConfiguration

| Property | Status |
|----------|--------|
| `Host` | ✅ Used |
| `Username` | ✅ Used |
| `Password` | ✅ Used |
| `VirtualHost` | ✅ Used |
| `RetryDelay` | ✅ Used |
| `MaxRetries` | ✅ Used |
| `PrefetchCount` | ✅ Used |
| `SslEnabled` | ✅ Used |
| `AcceptablePolicyErrors` | ✅ Used |
| `ServerName` | ✅ Used |
| `CertPath` | ✅ Used |
| `CertPassphrase` | ✅ Used |
| `Certs` | ✅ Used |
| `SslProtocol` | ✅ Used |
| `CertificateSelectionCallback` | ✅ Used |
| `CertificateValidationCallback` | ✅ Used |
| `ClientSettings` | ✅ Used |

### IQueueConfiguration

| Property | Status |
|----------|--------|
| `QueueName` | ✅ Used |
| `ErrorQueueName` | ✅ Used |
| `AuditQueueName` | ✅ Used |
| `HeartbeatQueueName` | ❌ Not Used - No heartbeat |
| `AuditingEnabled` | ✅ Used |
| `DisableErrors` | ✅ Used |
| `PurgeQueueOnStartup` | ✅ Used |
| `QueueMappings` | ✅ Used |

### IPersistenceConfiguration

| Property | Status |
|----------|--------|
| `ConnectionString` | ✅ Used |
| `DatabaseName` | ✅ Used |
| `AggregatorCollectionName` | ✅ Used |

### IPipelineConfiguration

| Property | Status |
|----------|--------|
| `BeforeConsumingFilters` | ✅ Used |
| `AfterConsumingFilters` | ✅ Used |
| `OutgoingFilters` | ✅ Used |
| `MessageProcessingMiddleware` | ❌ Not Used |
| `SendMessageMiddleware` | ❌ Not Used |

---

## Summary

| Feature | Status |
|---------|--------|
| Heartbeat System | ❌ **MISSING** |
| Process Manager Timeout Poller | ❌ **MISSING** |
| Middleware Pipeline | ❌ **NOT INVOKED** |
| ExceptionHandler | ❌ **NOT USED** |
| AutoStartConsuming | ❌ **NOT WORKING** |
| Clients Config | ❌ **NOT USED** |
| EnableProcessManagerTimeouts | ❌ **NOT USED** |
| PurgeQueueOnStartup | ✅ **WORKS** |
| Gzip Compression Filter | ✅ Exists |
| Redis Deduplication Persistor | ✅ Exists |

---

*Generated: 2026-04-11*
*Branch: improvements-and-fixes vs master*