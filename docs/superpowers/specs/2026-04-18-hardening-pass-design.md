## Summary

This design defines one coordinated hardening pass across the seven review findings discovered in ServiceConnect. The goal is to improve transport safety, request/reply correctness, persistence consistency, timeout delivery guarantees, and target-framework compatibility while keeping the public API stable where possible.

The work is intentionally scoped to existing messaging, persistence, and timeout code paths. It does not include unrelated refactoring or new public features.

## Goals

- Fix the `net8.0` compile break in the RabbitMQ client project.
- Tighten request/reply handling so forged or invalid reply traffic is not treated as trusted.
- Ensure reply messages pass through the same before-consume safety boundaries as other messages.
- Make process-manager persistence semantics consistent between in-memory and MongoDB backends.
- Prevent in-memory persistence from leaking handler mutations before successful persistence.
- Preserve timeout headers across store and dispatch.
- Reduce timeout duplicate-dispatch risk during cancellation or shutdown races.

## Non-Goals

- No broad redesign of the public messaging API.
- No unrelated cleanup outside transport/request-reply, persistence, timeout delivery, and the compile fix.
- No compatibility mode intended to preserve unsafe behavior by default.

## Scope

This hardening pass covers all seven findings as one internal remediation effort:

1. `net8.0` build break in `RabbitMqConsumerHost`.
2. Reply-destination validation bypass via untrusted `RequestMessageId`.
3. Reply messages bypassing before-consume filters.
4. MongoDB process-manager insert semantics differing from in-memory semantics.
5. In-memory process-manager reads returning live object references.
6. Timeout dispatch/remove race that can lead to duplicate delivery.
7. Timeout headers documented as preserved but dropped during dispatch.

## Design Principles

- Safety first: invalid, spoofed, or inconsistent behavior should fail explicitly instead of being silently accepted.
- Stable API: fixes should be implemented internally unless a public API change is unavoidable.
- Backend parity: in-memory and MongoDB behavior should match for duplicate handling, concurrency expectations, and timeout lifecycle semantics.
- Smallest effective change: prefer targeted changes in existing components over architectural churn.

## Architecture

### 1. Transport and request/reply hardening

The request/reply path will gain a stricter internal concept of trusted request ownership. A handler may bypass reply-destination validation only when the incoming request can be tied to a locally tracked outstanding request, not merely because headers contain a non-empty `RequestMessageId`.

Reply traffic will no longer short-circuit before the before-consume filter pipeline. The dispatcher will still recognize reply messages early enough to support requesters that do not register reply handlers, but only after before-consume filters have accepted the message.

Unknown or expired replies will no longer be treated as successfully handled. They will follow the normal failure path so the transport can retry, dead-letter, or audit them consistently.

### 2. Persistence consistency hardening

Process-manager persistence will be aligned so `InsertDataAsync` means insert across all backends. MongoDB duplicate inserts must fail rather than overwrite existing rows.

The in-memory process-manager finder will return detached copies instead of live references to stored state. This ensures handler-side mutations are only persisted after a successful update call and do not leak into storage if the handler throws or optimistic concurrency fails.

### 3. Timeout lifecycle hardening

Timeout dispatch will preserve stored headers when constructing the timeout send operation so timeout-delivered messages keep correlation, tenant, tracing, or security metadata that was intentionally persisted.

Timeout completion will use an explicit claim/complete-or-release lifecycle. A successfully sent timeout must not remain redispatchable simply because cancellation happened during post-send cleanup. Implementations should preserve lease ownership semantics where available and keep in-memory and MongoDB behavior aligned.

### 4. Build compatibility hardening

The RabbitMQ client project will replace the current `Lock` usage with a synchronization primitive supported on both `net8.0` and `net10.0`. This change should preserve current runtime behavior while restoring the advertised target-framework support.

## Behavioral Decisions

### `net8.0` build

`RabbitMqConsumerHost` will stop depending on a synchronization type unavailable to `net8.0`. The replacement must work on both target frameworks without changing public behavior.

### Trusted request/reply

Bypassing destination validation requires a verifiable local request/reply context. Raw header presence is insufficient.

The preferred design is to base trust on request manager state or another internal proof of local ownership already available on the consume path.

### Reply filtering

Reply messages must pass through before-consume filters before they are considered successfully handled by the reply manager.

### Unknown or expired replies

Replies that do not map to a valid pending request must not return success silently. They should be surfaced as invalid reply traffic within the existing failure handling model.

### Mongo insert semantics

MongoDB `InsertDataAsync` must fail on duplicates rather than overwrite existing documents or reset versions.

### In-memory data isolation

In-memory reads must return detached data so persistence occurs only through explicit successful update operations.

### Timeout completion

Timeout dispatch must preserve stored headers and use explicit completion/release behavior that prevents duplicate dispatch after a successful send.

### Backend parity

In-memory and MongoDB implementations should match as closely as practical for duplicate insert behavior, version/concurrency expectations, and timeout lease/completion semantics.

## Error Handling

The hardening pass will prefer explicit failure over silent success.

- Forged or untrusted reply traffic should fail through the normal consume failure path.
- Unknown or expired replies should not be acknowledged as successful processing.
- Duplicate persistence inserts should fail consistently across backends.
- Timeout cleanup failures after send should preserve correctness first, even if they require explicit release or retry behavior.

This preserves the framework's existing retry, audit, and error-queue behavior instead of inventing parallel failure semantics for these cases.

## Testing Strategy

Regression coverage must be added at the subsystem boundaries affected by the fixes.

### Build verification

- Build `ServiceConnect.Client.RabbitMQ` for `net8.0`.
- Keep existing target frameworks building after the synchronization change.

### Request/reply and transport tests

- Verify reply messages still reach the request/reply manager when valid.
- Verify before-consume filters execute for reply messages.
- Verify forged `RequestMessageId` headers do not bypass destination validation.
- Verify unknown or expired replies are not reported as successful handling.

### Persistence tests

- Verify MongoDB duplicate insert behavior matches in-memory duplicate rejection.
- Verify in-memory reads return detached data rather than live references.
- Verify failed handler or failed update paths do not leak in-memory state mutations.

### Timeout tests

- Verify persisted timeout headers are included when dispatching timeout messages.
- Verify successful timeout sends are not redispatched because cancellation interrupted cleanup.
- Verify lease-aware completion/release behavior remains correct in both in-memory and MongoDB paths.

## Rollout and Compatibility

This change set should be released as a hardening/bug-fix pass.

- Public APIs should remain stable unless an implementation detail proves impossible without a surface change.
- Unsafe behavior should not be preserved merely for compatibility.
- Release notes should call out stricter reply validation, reply filter enforcement, backend persistence parity, and timeout header preservation as bug fixes.

## Risks and Trade-Offs

- Some previously tolerated but unsafe reply flows may begin failing once trust checks and filter enforcement are applied.
- Tightening MongoDB insert semantics may expose callers that were implicitly relying on overwrite-on-insert behavior.
- Returning detached copies from in-memory persistence may surface tests or code that accidentally depended on live-reference behavior.
- Timeout completion changes must be validated carefully so they prevent duplicates without introducing stuck leases.

These are acceptable trade-offs because the current behaviors create correctness and security problems.

## Success Criteria

This hardening pass is complete when all of the following are true:

- The RabbitMQ client project builds successfully for `net8.0` and `net10.0`.
- Reply messages no longer bypass before-consume filters.
- Forged reply-related headers do not bypass destination validation.
- Unknown or expired replies are no longer silently accepted as success.
- MongoDB and in-memory process-manager duplicate insert behavior match.
- In-memory process-manager reads no longer expose live persisted instances.
- Timeout-dispatched messages preserve stored headers.
- Successful timeout sends are not duplicated because of cleanup cancellation races.
