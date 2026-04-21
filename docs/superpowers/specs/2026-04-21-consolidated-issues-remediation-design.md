---
name: Consolidated issues remediation — v7 branch
description: Plan for fixing all 62 verified issues in consolodated-issues/2026-04-21-consolidated-issues.md on the v7-clean-architecture branch
type: design
date: 2026-04-21
branch: v7-clean-architecture
---

# Consolidated Issues Remediation — Design

## Goal

Fix all 62 verified issues from [consolodated-issues/2026-04-21-consolidated-issues.md](../../../consolodated-issues/2026-04-21-consolidated-issues.md) on the `v7-clean-architecture` branch. 12 Critical, 20 High, 23 Medium, 7 Low.

## Constraints (from brainstorming)

- **Scope:** all 62 issues on this single branch. No worktrees.
- **Commit granularity:** one commit per logical group. ~32 groups.
- **Test rigour:** Critical + High groups get new tests that would have caught the bug. Medium + Low rely on existing suite.
- **Execution:** per-group checkpoint — plan, approve, implement, test, report, approve, commit.
- **Tracking:** checkboxes in the consolidated issues doc itself, updated in the same commit as each fix.
- **No PR until all groups complete** (unless user changes their mind mid-flight).

## Phasing & Groups

Each **G#** is one commit. Ordering follows the "Recommended fix order" in the consolidated doc.

### Phase 1 — Trust boundary (Critical)
- **G1:** C1 + C5 + C9 + H7 — reserved headers server-authoritative; whitelist-only reply type resolution; reply routing tied to verified config.

### Phase 2 — Critical correctness / redelivery / observability
- **G2:** C2 + C4 — `HeaderDecoder` tolerates unexpected AMQP header types; audit-publish failure no longer fails delivery.
- **G3:** C3 + C6 — saga mapping throws on unsupported expression shapes; `NotHandled` becomes a distinct dispatcher state.
- **G4:** C7 + C8 — remove/refresh 2-day absolute expiry on in-memory saga + aggregator state.
- **G5:** C10 — process-manager `DeleteDataAsync` filters by `{CorrelationId, Version}` on both backends.
- **G6:** C11 + C12 — inject `traceparent` on outbound; stamp `CorrelationId` header on outbound.

### Phase 3 — Pipeline & DI (High)
- **G7:** H2 — `MessageDispatcher` honours the `messageType` parameter; header becomes tie-break.
- **G8:** H3 + H4 + M12 — scoped provider for inbound middleware/filters; startup validation for inbound pipelines (mirrors send-side).
- **G9:** H5 — `RegisterHandlerType` skips if any registration exists.
- **G10:** H6 — wrap reply + stream-complete dispatch in the same filter/middleware chain as regular consume.

### Phase 4 — RabbitMQ transport (High)
- **G11:** H1 — publisher confirms on helper channel.
- **G12:** H8 + H14 + M23 — `SendBytesAsync` endpoint validation; `RetryCount` parse failure routes to error; prefetch cast handles non-int boxed values.
- **G13:** H9 + H10 + H11 + H12 — cancellation-token plumbing; consumer-lifetime token; ordered startup; ownership-aware connection dispose.
- **G14:** H20 — producer honours configured heartbeat settings.

### Phase 5 — Persistence & processors (High)
- **G15:** H13 — aggregator remove-before-execute.
- **G16:** H16 — split side-effect-free state transition from side-effecting work in PM retry.
- **G17:** H17 + H18 + H19 — `IAsyncDisposable` on InMemory persistors + state; deep-clone on read/write.
- **G18:** H21 — Mongo PM `Version` restore covers all failure paths.

### Phase 6 — Medium API/shape
- **G19:** M1 + M2 — `SendAsync` endpoint+endpoints validation; per-element queue mapping validation.
- **G20:** M3 + M4 + M20 — type identity via `AssemblyQualifiedName`/`FullName` (queue mappings, registry, Mongo collections).
- **G21:** M5 — stronger sanitizer for exchange/binding names.
- **G22:** M8 + M9 + M10 + M11 — `Context` non-nullable; `PublishOptions` as `readonly record struct`; read-only headers; snapshot `OutgoingEventArgs.Headers`.

### Phase 7 — Medium: telemetry + timeout store + cache
- **G23:** M6 + M7 — trace extraction iterates interface; `publish` vs `send` semconv.
- **G24:** M13 + M14 + M15 + M16 + M17 — timeout store correctness (leased-due, lock owner, batch limit, unique id, deterministic order).
- **G25:** M18 — Mongo timeout lease reaper.
- **G26:** M19 — Mongo TLS protocol/revocation apply whenever TLS enabled.
- **G27:** M21 + M22 — `CacheProvider` TTL on re-add; consistent `KeyRemoved` semantics.

### Phase 8 — Low: hardening
- **G28:** L1 + L2 — per-element null check; `RequestOptions.Default` immutable.
- **G29:** L3 + L4 — stream `Length`/`CanSeek` contract; CA1032 exception ctors.
- **G30:** L5 — `IsEnabled(Debug)` guards on hot paths.
- **G31:** L6 — audit routing key configurable.
- **G32:** L7 — align `PublishRequestAsync` parameter order.

**H15** is absent from the consolidated doc by design; a one-line footnote will be added to the doc to prevent confusion.

## Per-group workflow

For each group:

1. **Plan** — I present files-to-change, approach, risks, tests to add (Critical/High). ~5-15 lines.
2. **Approval gate** — user approves or redirects.
3. **Implement** — edits go in; comments stay minimal per project CLAUDE.md style.
4. **Tests** — Critical/High: new tests that would have caught the bug. Medium/Low: existing suite only.
5. **Verify** — run unit tests; run integration tests where fix touches transport or Mongo. Docker via `sg docker -c`.
6. **Report** — summary of changes, test results, updated checkboxes.
7. **Review gate** — user reviews diff and test output.
8. **Commit** — single commit on approval; consolidated-doc checkboxes updated in same commit.

**Mid-group surprises:** if scope grows unexpectedly, stop and re-plan. If a second bug surfaces, flag and defer by default.

**No cross-group drift:** each commit only touches files relevant to that group.

**Rollback unit:** `git revert <sha>` reverses exactly one group.

## Commit message convention

```
<type>(<area>): <short summary referencing issue IDs>

<body: one sentence per issue covered, stating what changed and why.>

Refs: C1, C5, C9, H7
```

- `<type>`: `fix`, `refactor`, or `chore` (matches existing repo convention).
- `<area>`: `rabbitmq`, `dispatcher`, `persistence-inmem`, `persistence-mongo`, `telemetry`, `di`, `interfaces`, etc.
- `Refs:` footer lists the issue IDs the commit closes.

## Progress tracking

The consolidated doc itself is living state. Each issue heading gains a checkbox prefix:

- `[ ]` — pending
- `[~]` — in progress (set when group plan is approved)
- `[x] (commit: <sha>)` — done
- `[-]` — deferred, with one-line reason

A summary block at the top of the doc is updated in the same commit as each fix:

```markdown
**Progress:** Critical 6/12 · High 4/20 · Medium 0/23 · Low 0/7  (updated 2026-04-21)
```

## Test strategy

| Group kind | Unit | Integration |
|---|---|---|
| Dispatcher / pipeline / DI (G3, G7-G10) | ✅ | ❌ |
| InMemory persistence (G4, G15, G17) | ✅ | ❌ |
| RabbitMQ transport (G1, G2, G6, G11-G14) | ✅ (header logic) | ✅ (confirm/publish/consume flows) |
| Mongo persistence (G5, G18, G24, G25) | ❌ | ✅ |
| Telemetry (G6, G23) | ✅ | ❌ |

**Commands:**
- Unit: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`
- Integration: `sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj'`

**Test mutation:** allowed only where an existing test asserts buggy behaviour. Flagged explicitly in the group report.

**Untestable fixes:** if a bug can't be reproduced cleanly in a test (e.g. multi-process trust decisions), I propose either an integration test or an internal-visible unit test exercising the trust path directly; user decides.

## Pre-work (one-off before G1)

1. **Baseline green** — run both test suites on current HEAD. Record pass/skip/fail. If red, fix first.
2. **Docker smoke** — confirm `sg docker -c 'docker ps'` works and Testcontainers can pull RabbitMQ + MongoDB images.
3. **Checkboxes added** — add `[ ]` prefix to every issue heading; add progress summary block; add H15 footnote. Single commit: `chore(tracking): add progress checkboxes to consolidated issues doc`. No code changes.
4. **Branch confirmation** — verify on `v7-clean-architecture`.

## Exit condition

- All 62 issue checkboxes ticked.
- Both test suites green.
- Progress summary reads `Critical 12/12 · High 20/20 · Medium 23/23 · Low 7/7`.

## Out of scope

- PR creation / merge — user decides when to PR.
- Release notes / changelog.
- Any new feature work or refactoring beyond what each fix requires.
- Issues surfaced mid-flight that aren't in the consolidated doc — logged, deferred by default.
