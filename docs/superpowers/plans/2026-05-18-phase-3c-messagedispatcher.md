# Phase 3c — MessageDispatcher.DispatchAsync Split Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decompose the ~200-line `MessageDispatcher.DispatchAsync` into a thin coordinator backed by three focused private helpers (`TryResolveMessageType`, `RunDispatchPipelineAsync`, `HandleDispatchErrorAsync`), and rename the six single-letter parameters in `RunProcessors` / the lambda inside `BuildProcessingChain`. Behaviour-preserving refactor.

**Architecture:** Three helpers added to `MessageDispatcher` (all `private`, no new collaborators, no public API change). Each helper owns one concern: type-name candidate collection + registry resolution + unregistered-fallback logic (`TryResolveMessageType`); the main pipeline from before-filter through dispatch (`RunDispatchPipelineAsync`); error classification + handler invocation (`HandleDispatchErrorAsync`). The OCE catch in `DispatchAsync` stays at the outer level — it's a re-throw, not an error to classify. As a drive-by surfaced by the extraction, the dead `if (!typeResolvedFromRegistry) return NotHandled` re-check at the original line 171-176 is removed (it is unreachable: if the type wasn't registry-resolved and the message has no `ResponseMessageId`, we already returned at line 160; otherwise we set `type = typeof(Message)` and the subsequent `hasResponseMessageId` branch at line 166 already returned).

**Tech Stack:** C# 12/14 (multi-target net8.0/net10.0; tests net10.0), xUnit 2.9, Moq 4.20. No new dependencies.

---

## Phase-wide rules (apply to EVERY task)

These rules live in [docs/reviews/2026-05-17-refactor-phasing.md](../../reviews/2026-05-17-refactor-phasing.md) and the workflow established by Phase 1+2; repeated here so the implementer never has to leave this document.

1. **Validate before implementing.** Step 1 of every task is "Validate the scope." Re-read the cited lines and confirm no concurrent work has started the extraction. If a finding is no longer real (e.g. someone has already extracted one of the helpers), append an entry to `docs/reviews/2026-05-17-rejected-findings.md` and skip the remaining steps.
2. **No `dotnet` from the main session.** Every `dotnet build` / `dotnet test` step is invoked from the subagent owning the task.
3. **Per-csproj invocations only.** Target `src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj` for the regression net — never the solution.
4. **`-m:1` at MSBuild level** on every `dotnet` command. Non-negotiable.
5. **Per-change code review.** After each task's implementation passes its tests, invoke `superpowers:requesting-code-review` on the change before committing.
6. **Commit one task = one commit.** Scoped prefix (`refactor(dispatch):`, `test(dispatch):` etc.), present-tense summary, Claude co-author trailer.
7. **No ticket / phase / "fixes Xxx" framing in source comments** (per project CLAUDE.md). The plan file in `docs/superpowers/plans/` is documentation, allowed to mention phases.
8. **Behaviour-preserving refactor.** The existing `MessageDispatcherTests.cs` (1114 lines, 32+ `[Fact]` tests) plus the broader Bus + E2E suites are the regression net. Every refactor task ends by running the full `MessageDispatcher` test class — zero failures expected. A pre-existing test failure means observable behaviour changed and must be reverted/fixed before commit.

---

## End-of-phase verification gate (run after all tasks complete)

- [ ] **Final code review** — run `superpowers:requesting-code-review` on the full Phase 3c diff (base = HEAD at start of Phase 3c, head = current HEAD).
- [ ] **Full unit-test suite** — delegated subagent runs `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1`. Phase 2 baseline: 1644 tests, all green.
- [ ] **Full end-to-end suite** — `dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`. Phase 2 baseline: 136/136.
- [ ] **Serialization compat tests** — `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj -m:1`. Phase 2 baseline: 48/48.
- [ ] **All example apps build** — `find examples -name '*.csproj' -print0 | xargs -0 -I{} dotnet build "{}" -m:1`. Phase 2 baseline: 53/53.
- [ ] **Documentation site current** — verify `website/src/content/docs/` reflects any user-visible behaviour change. Phase 3c is internal-only; expected NO UPDATE.
- [ ] **README current** — same.
- [ ] **MessageDispatcher.cs LOC sanity** — record before/after line count. `DispatchAsync` should be ~50-70 lines after the three helpers absorb the body; total file LOC may stay roughly flat (helper bodies absorb removed inline code).

---

## File structure

| File | Change | Responsibility |
|---|---|---|
| `src/ServiceConnect/Services/MessageDispatcher.cs` | Modify | Rename `RunProcessors` parameters; add `TryResolveMessageType`, `HandleDispatchErrorAsync`, `RunDispatchPipelineAsync` helpers; shrink `DispatchAsync` to coordinator. |

No new files. No new types. No new collaborators. No public API change. The existing `MessageDispatcherTests.cs` and its siblings are the regression net — they are NOT modified.

---

## Task 1: Validate the scope

**No code changes — investigation and confirmation only.**

- [ ] **Step 1: Confirm DispatchAsync is still the ~200-line method**

```bash
cd /home/tim/source/ServiceConnect-CSharp
grep -n "public async Task<ConsumeEventResult> DispatchAsync\|private async Task<ConsumeEventResult> DispatchReplyAsync" src/ServiceConnect/Services/MessageDispatcher.cs
wc -l src/ServiceConnect/Services/MessageDispatcher.cs
```

Expected: `DispatchAsync` declared at line 54, `DispatchReplyAsync` declared at line 258 (so `DispatchAsync` body spans ~200 lines). Total file ~416 lines. If significantly different, STOP and report NEEDS_CONTEXT.

- [ ] **Step 2: Confirm RunProcessors still has single-letter parameters**

```bash
grep -n "private async Task<ConsumeEventResult> RunProcessors" src/ServiceConnect/Services/MessageDispatcher.cs
```

Expected signature line at ~370 reads:
```csharp
private async Task<ConsumeEventResult> RunProcessors(ReadOnlyMemory<byte> mb, Type mt, object m, IDictionary<string, object> h, Envelope e, CancellationToken ct)
```

If parameters have already been renamed, append to `docs/reviews/2026-05-17-rejected-findings.md` and skip Task 2 only.

- [ ] **Step 3: Confirm no helper already exists**

```bash
grep -n "TryResolveMessageType\|RunDispatchPipelineAsync\|HandleDispatchErrorAsync" src/ServiceConnect/Services/MessageDispatcher.cs src/ServiceConnect.UnitTests/MessageDispatcherTests.cs
```

Expected: zero matches. If any of the three names already exist, STOP and report — someone has started the extraction.

- [ ] **Step 4: Confirm the dead-code branch at line ~171-176**

Read `src/ServiceConnect/Services/MessageDispatcher.cs:166-178`. The block:
```csharp
if (hasResponseMessageId)
{
    return await DispatchReplyAsync(...).ConfigureAwait(false);
}

if (!typeResolvedFromRegistry)
{
    return new ConsumeEventResult { Success = true, NotHandled = true };
}
```

Reason through it: by line 171 we know `hasResponseMessageId == false` (otherwise we returned at the prior block). The earlier branch at lines 153-164 either returned `NotHandled` when `!typeResolvedFromRegistry && !hasResponseMessageId`, or set `type = typeof(Message)` when `!typeResolvedFromRegistry && hasResponseMessageId`. Combined: by line 171 we have `hasResponseMessageId == false` AND `typeResolvedFromRegistry == true`. The `if (!typeResolvedFromRegistry)` check is unreachable. Confirm this reasoning by reading the code first-hand.

If the conclusion holds, the dead branch will be removed during Task 3's extraction. If you find a path that DOES reach line 171 with `typeResolvedFromRegistry == false`, STOP and report — the reasoning is wrong and the plan needs revision.

- [ ] **Step 5: Record baseline**

```bash
wc -l src/ServiceConnect/Services/MessageDispatcher.cs
```

Note value (~416). Used in the end-of-phase LOC check.

- [ ] **Step 6: Report**

Report Status: DONE with the actual counts/line numbers. No commit for this task.

---

## Task 2: Rename single-letter params in `RunProcessors` (and the lambda in `BuildProcessingChain`)

**Mechanical, behaviour-preserving rename.**

**Files:**
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs:370-415`

Reads better; no logic change. The plan deliberately does NOT touch the `var next = chain;` local in `BuildProcessingChain` — the review's "lambda captures `next` reused across loop iterations" concern is technically incorrect (the `var next` is declared inside the `for` body, so each iteration creates a fresh local; each lambda captures its own iteration's local correctly). Renaming `next` would be churn for a non-issue.

- [ ] **Step 1: Validate**

Confirm the current shape of `RunProcessors` and `BuildProcessingChain`:

```bash
sed -n '370,415p' src/ServiceConnect/Services/MessageDispatcher.cs
```

If the lambda inside `BuildProcessingChain` (currently at line 412) is `(mb, mt, m, h, e, ct) => mw.ProcessAsync(mb, mt, m, h, e, next, ct)`, proceed. If parameters already renamed, skip Task 2 and proceed to Task 3.

- [ ] **Step 2: Rename in `RunProcessors`**

Replace lines 370-396 (signature + body) with renamed parameters. Before:

```csharp
private async Task<ConsumeEventResult> RunProcessors(ReadOnlyMemory<byte> mb, Type mt, object m, IDictionary<string, object> h, Envelope e, CancellationToken ct)
{
    foreach (var proc in _processors)
    {
        ct.ThrowIfCancellationRequested();

        if (proc.RunBeforeDeserialization)
        {
            continue;
        }

        var result = await proc.ProcessAsync(mb, mt, m, h, e, ct).ConfigureAwait(false);
        if (result == ProcessResult.Handled)
        {
            return new ConsumeEventResult { Success = true };
        }
    }

    _logger.LogDebug("No processor handled message of type {MessageType}", mt.FullName);
    return new ConsumeEventResult { Success = true, NotHandled = true };
}
```

After:

```csharp
private async Task<ConsumeEventResult> RunProcessors(
    ReadOnlyMemory<byte> messageBytes,
    Type messageType,
    object message,
    IDictionary<string, object> headers,
    Envelope envelope,
    CancellationToken cancellationToken)
{
    foreach (var proc in _processors)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (proc.RunBeforeDeserialization)
        {
            continue;
        }

        var result = await proc.ProcessAsync(messageBytes, messageType, message, headers, envelope, cancellationToken).ConfigureAwait(false);
        if (result == ProcessResult.Handled)
        {
            return new ConsumeEventResult { Success = true };
        }
    }

    _logger.LogDebug("No processor handled message of type {MessageType}", messageType.FullName);
    return new ConsumeEventResult { Success = true, NotHandled = true };
}
```

(Note `mt.FullName` → `messageType.FullName` at the log call.)

- [ ] **Step 3: Rename in the `BuildProcessingChain` lambda**

Replace line 412 (the lambda body inside the for loop). Before:

```csharp
chain = (mb, mt, m, h, e, ct) => mw.ProcessAsync(mb, mt, m, h, e, next, ct);
```

After:

```csharp
chain = (messageBytes, messageType, message, headers, envelope, cancellationToken) =>
    mw.ProcessAsync(messageBytes, messageType, message, headers, envelope, next, cancellationToken);
```

Leave the surrounding `var mw = ...`, `var next = chain;` lines untouched.

- [ ] **Step 4: Build**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
```

Expected: 0 errors, 0 warnings (TreatWarningsAsErrors=true).

- [ ] **Step 5: Run the full MessageDispatcher* test class**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~MessageDispatcher -m:1
```

Expected: every pre-existing MessageDispatcher test passes. Rename is purely syntactic — no behavioural change possible.

- [ ] **Step 6: Per-change code review**

Invoke `superpowers:requesting-code-review` on the staged diff. The review focus is "is this change purely a syntactic rename, or did anything sneak through?"

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect/Services/MessageDispatcher.cs
git commit -m "$(cat <<'EOF'
refactor(dispatch): rename single-letter params in RunProcessors

mb → messageBytes, mt → messageType, m → message, h → headers,
e → envelope, ct → cancellationToken. Same names applied to the
middleware-chain lambda inside BuildProcessingChain. Behaviour-
preserving rename; full MessageDispatcher suite green.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Extract `TryResolveMessageType`

**Files:**
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs` (DispatchAsync body + add helper)

**Finding context:** The 3-candidate decoding (`primaryCandidate` / `fullTypeNameCandidate` / `typeNameCandidate`) at lines 76-88, plus the registry resolution + unregistered-fallback at lines 146-164, plus the dead re-check at lines 171-176, all belong to one concern: "given the inbound delivery, what `Type` should we dispatch against (or is this a terminal not-handled)?".

The helper returns a small result struct. Caller becomes a single conditional + the result-typed local.

- [ ] **Step 1: Validate**

Re-read `MessageDispatcher.cs:76-95` and `146-176`. Confirm the candidate set + registry resolution + dead-recheck shape are still present. Confirm `hasResponseMessageId` (line 119) is still computed before the registry resolution branch (the helper needs it).

- [ ] **Step 2: Add the result struct + helper**

Insert near the bottom of the `MessageDispatcher` class, alongside `RunPreDeserializationProcessorsAsync` / `RunProcessors`. Suggested spot: just above `RunPreDeserializationProcessorsAsync` (line 338). Insert:

```csharp
/// <summary>
/// Outcome of resolving the inbound delivery's message-type. When <see cref="Type"/> is non-null
/// the caller dispatches against it (it may be <c>typeof(Message)</c> on the reply-path fallback);
/// when <see cref="ShouldReturnNotHandled"/> is true the caller returns a Success+NotHandled
/// result immediately (terminal — no retry, no dispatch).
/// </summary>
private readonly record struct MessageTypeResolution(Type? Type, bool ShouldReturnNotHandled);

/// <summary>
/// Resolves the type to dispatch against from the inbound delivery's <paramref name="messageType"/>
/// argument and the FullTypeName / TypeName headers, falling back to <c>typeof(Message)</c> for
/// the reply path (<paramref name="hasResponseMessageId"/> == true) when the registry doesn't
/// know the type.
/// </summary>
/// <remarks>
/// Unregistered + not-a-reply is a terminal not-handled: retrying never resolves it, so the caller
/// routes to dead-letter (when configured) or ack-and-drops rather than burning the full retry
/// budget through Success=false → nack/requeue.
/// </remarks>
private MessageTypeResolution TryResolveMessageType(
    string messageType,
    IReadOnlyDictionary<string, object> headers,
    bool hasResponseMessageId)
{
    string? primaryCandidate = string.IsNullOrWhiteSpace(messageType) ? null : messageType;
    string? fullTypeNameCandidate = headers.TryGetValue(HeaderKeys.FullTypeName, out var fullTypeNameRaw)
        ? HeaderDecoder.Decode(fullTypeNameRaw) : null;
    string? typeNameCandidate = headers.TryGetValue(HeaderKeys.TypeName, out var typeNameRaw)
        ? HeaderDecoder.Decode(typeNameRaw) : null;

    if (primaryCandidate is null && fullTypeNameCandidate is null && typeNameCandidate is null)
    {
        throw new InvalidOperationException(
            "Message is missing type information: messageType parameter is empty and neither FullTypeName nor TypeName header is present.");
    }

    var fullTypeName = primaryCandidate ?? fullTypeNameCandidate ?? typeNameCandidate!;

    Type? type = null;
    bool typeResolvedFromRegistry =
        (primaryCandidate is not null && _typeRegistry.TryResolve(primaryCandidate, out type))
        || (fullTypeNameCandidate is not null && _typeRegistry.TryResolve(fullTypeNameCandidate, out type))
        || (typeNameCandidate is not null && _typeRegistry.TryResolve(typeNameCandidate, out type));

    if (typeResolvedFromRegistry)
    {
        return new MessageTypeResolution(type, ShouldReturnNotHandled: false);
    }

    if (hasResponseMessageId)
    {
        // Reply with unregistered payload type: the dispatcher can still route the reply via
        // DispatchReplyAsync. The base Message type is the conservative deserialise target;
        // the matched pending request's ReplyType supplies the real shape downstream.
        return new MessageTypeResolution(typeof(Message), ShouldReturnNotHandled: false);
    }

    _logger.LogWarning("Unregistered message type '{TypeName}'. Routing as not-handled.", fullTypeName);
    return new MessageTypeResolution(Type: null, ShouldReturnNotHandled: true);
}
```

- [ ] **Step 3: Migrate `DispatchAsync`**

Replace the block at lines 76-95 + lines 146-176 with a single call. The caller code becomes (read carefully — the post-replace shape spans what was previously two non-contiguous blocks):

Old shape (approximate, lines 76-95 then 146-176):

```csharp
// lines 76-88
string? fullTypeName = null;
string? primaryCandidate = ...;
string? fullTypeNameCandidate = ...;
string? typeNameCandidate = ...;
if (primaryCandidate is null && fullTypeNameCandidate is null && typeNameCandidate is null) { throw ... }
fullTypeName = primaryCandidate ?? fullTypeNameCandidate ?? typeNameCandidate!;

// ... (other code, including hasResponseMessageId at line 119)

// lines 146-176
Type? type = null;
bool typeResolvedFromRegistry = ...;
if (!typeResolvedFromRegistry)
{
    if (!hasResponseMessageId)
    {
        _logger.LogWarning("Unregistered message type ...", fullTypeName);
        return new ConsumeEventResult { Success = true, NotHandled = true };
    }
    type = typeof(Message);
}

if (hasResponseMessageId)
{
    return await DispatchReplyAsync(replyProcessor, messageBytes, type!, mutableHeaders, envelope, headers, cancellationToken).ConfigureAwait(false);
}

if (!typeResolvedFromRegistry)   // DEAD: removed
{
    return new ConsumeEventResult { Success = true, NotHandled = true };
}
```

New shape (replace BOTH the upfront block and the registry-resolution block):

```csharp
// (at the position previously occupied by lines 76-88 — early in DispatchAsync)
// — remove the upfront candidate-decode block entirely. The helper will do it.

// (at the position previously occupied by lines 146-176, after hasResponseMessageId is computed)
var typeResolution = TryResolveMessageType(messageType, headers, hasResponseMessageId);
if (typeResolution.ShouldReturnNotHandled)
{
    return new ConsumeEventResult { Success = true, NotHandled = true };
}
var type = typeResolution.Type!;

if (hasResponseMessageId)
{
    return await DispatchReplyAsync(replyProcessor, messageBytes, type, mutableHeaders, envelope, headers, cancellationToken).ConfigureAwait(false);
}
```

**Important details:**
- The upfront candidate-decode block (lines 76-88) is removed entirely — the helper does this work, called later.
- The dead `if (!typeResolvedFromRegistry) return NotHandled` re-check is removed.
- The `fullTypeName` local that was used at the now-removed log site (line 159 "Unregistered message type") is no longer needed in `DispatchAsync` — the log now lives in `TryResolveMessageType`.
- `type!` post-helper uses null-forgiving because `ShouldReturnNotHandled=false` implies `Type != null` by contract; the xmldoc captures this. Note this matches the existing `null!` convention used in Phase 2's `OutboundPreparation`.

The `RunPreDeserializationProcessorsAsync` call at line 133 (`var (replyProcessor, preDeserHandled) = await RunPreDeserializationProcessorsAsync(...)`) and everything BEFORE it stays untouched. Only the candidate-decode block above it and the registry-resolution block after it move.

Also note: the `messageType` log at the catch blocks (lines 213, 219) used to reference the local `messageType` parameter, which is unchanged. They still work.

- [ ] **Step 4: Build**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 5: Run the MessageDispatcher tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~MessageDispatcher -m:1
```

Expected: all pre-existing tests pass.

Likely failure modes if a test fails:
- Forgot to remove the upfront candidate-decode block (compile error from duplicate `primaryCandidate`).
- Removed `fullTypeName` local but the log message still referenced it elsewhere.
- The `type!` non-null assertion fires because the helper returns `(null, false)` somewhere — re-read the helper's branches.
- The "missing type information" throw lives in the helper now — confirm a test that asserts this exception is still satisfied.

- [ ] **Step 6: Per-change code review**

Invoke `superpowers:requesting-code-review`. Highlight in the dispatch prompt:
- Behavioural equivalence (every old branch maps to a new branch).
- Dead-code removal is intentional and reasoned through in the plan.
- `type!` null-forgiving is contract-defended.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect/Services/MessageDispatcher.cs
git commit -m "$(cat <<'EOF'
refactor(dispatch): extract TryResolveMessageType helper

The 3-candidate type-name decoding (messageType param, FullTypeName
header, TypeName header) + registry resolution + unregistered-fallback
now live in a single private helper. DispatchAsync drops the upfront
candidate-decode block and the dead `if (!typeResolvedFromRegistry)
return NotHandled` re-check that was unreachable after the prior
reply-path branch handled the only path that could leave the flag
false (reply path sets type = typeof(Message); non-reply unregistered
path returned earlier).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Extract `HandleDispatchErrorAsync`

**Files:**
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs` (DispatchAsync catch blocks + add helper)

**Finding context:** The two catch blocks (JsonException-class terminal at lines 200-216, generic at lines 217-222) both: log + invoke exception handler + return a `ConsumeEventResult`. They differ only in the log severity wording, the `TerminalFailure` flag, and the rationale comment. Collapse into one helper that classifies the exception, logs, invokes the handler, returns the result. The OCE catch (line 193) stays at the outer level — it's a re-throw, not an error to classify.

- [ ] **Step 1: Validate**

Re-read lines 193-222. Confirm:
- OCE catch is a bare re-throw (line 198).
- Two `Exception` catches: one filtered by `when (ex is JsonException or NotSupportedException or Interfaces.Exceptions.SerializationException)`, one bare.
- Both bare-exception branches call `InvokeExceptionHandlerAsync(ex, messageType, cancellationToken)` and return a `ConsumeEventResult` with `Exception = ex`. The terminal branch additionally sets `TerminalFailure = true`.

- [ ] **Step 2: Add the helper**

Place near `InvokeExceptionHandlerAsync` (line 318) — they're both error-path helpers. Suggested spot: directly above `InvokeExceptionHandlerAsync`. Insert:

```csharp
/// <summary>
/// Classifies a dispatch exception as terminal (permanently invalid payload) vs transient,
/// logs at the appropriate severity, invokes the user-supplied exception handler, and
/// produces the <see cref="ConsumeEventResult"/> the dispatcher returns. The OCE re-throw
/// case is handled at the call site — it's not an "error" in this sense.
/// </summary>
private async Task<ConsumeEventResult> HandleDispatchErrorAsync(
    Exception ex,
    string messageType,
    CancellationToken cancellationToken)
{
    if (ex is JsonException or NotSupportedException or Interfaces.Exceptions.SerializationException)
    {
        // Permanently malformed payload — JsonException covers wire-format faults
        // (truncated bytes, schema mismatch, max-depth exceeded), NotSupportedException
        // surfaces when an STJ converter rejects the value, and SerializationException
        // is the serializer's wrapper around JsonException. Retrying produces the
        // identical failure; route as terminal so the message goes straight to the error
        // exchange and the retry budget isn't burned on a poison delivery.
        _logger.LogError(ex,
            "Permanently invalid payload for message of type {MessageType}; routing as terminal failure (no retry).",
            messageType);
        await InvokeExceptionHandlerAsync(ex, messageType, cancellationToken).ConfigureAwait(false);
        return new ConsumeEventResult { Success = false, Exception = ex, TerminalFailure = true };
    }

    _logger.LogError(ex, "Error dispatching message of type {MessageType}", messageType);
    await InvokeExceptionHandlerAsync(ex, messageType, cancellationToken).ConfigureAwait(false);
    return new ConsumeEventResult { Success = false, Exception = ex };
}
```

- [ ] **Step 3: Replace the catch bodies in `DispatchAsync`**

Replace lines 200-222 (both `catch (Exception ex)` blocks) with a single bare-exception catch:

Before (approximate):
```csharp
catch (Exception ex) when (ex is JsonException or NotSupportedException or Interfaces.Exceptions.SerializationException)
{
    // (comment)
    _logger.LogError(ex, "Permanently invalid payload ...", messageType);
    await InvokeExceptionHandlerAsync(ex, messageType, cancellationToken).ConfigureAwait(false);
    return new ConsumeEventResult { Success = false, Exception = ex, TerminalFailure = true };
}
catch (Exception ex)
{
    _logger.LogError(ex, "Error dispatching message of type {MessageType}", messageType);
    await InvokeExceptionHandlerAsync(ex, messageType, cancellationToken).ConfigureAwait(false);
    return new ConsumeEventResult { Success = false, Exception = ex };
}
```

After:
```csharp
catch (Exception ex)
{
    return await HandleDispatchErrorAsync(ex, messageType, cancellationToken).ConfigureAwait(false);
}
```

Leave the OCE catch (line 193-199) untouched. Leave the `finally` block (lines 223-241) untouched.

- [ ] **Step 4: Build**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
```

- [ ] **Step 5: Run the MessageDispatcher tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~MessageDispatcher -m:1
```

Expected: all green. Watch in particular for tests that assert the `TerminalFailure` flag on JsonException-class failures — those tests verify the discrimination logic and will catch a regression if `HandleDispatchErrorAsync` misclassifies.

- [ ] **Step 6: Per-change code review**

`superpowers:requesting-code-review`. Highlight that exception-type classification is unchanged.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect/Services/MessageDispatcher.cs
git commit -m "$(cat <<'EOF'
refactor(dispatch): extract HandleDispatchErrorAsync helper

DispatchAsync's two non-OCE catch blocks (JsonException-class terminal
failures vs generic exceptions) collapse to a single bare catch that
delegates to the new helper. Exception classification (the
JsonException / NotSupportedException / SerializationException
discrimination), log severity, and ConsumeEventResult shape are
preserved exactly. The OCE re-throw catch stays at the call site —
it's not an error to classify.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Extract `RunDispatchPipelineAsync`

**Files:**
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs` (the bulk of DispatchAsync's body + add helper)

**Finding context:** After Tasks 3 and 4 the remaining body of `DispatchAsync` is approximately: outer scope creation → consume-context push → before-filter → pre-deser processors → type resolution (helper call) → reply branch dispatch → deserialize + chain → main dispatch → on-consumed-successfully → after-filter (in finally). This is still ~120 lines of pipeline orchestration. Extract the pipeline (from before-filter through on-consumed-successfully) into a single helper so `DispatchAsync` becomes a thin coordinator.

This is the biggest extraction in Phase 3c. Approach it carefully: do it AFTER Tasks 2-4 have shrunk the method (so the diff is readable), and lean on the existing `MessageDispatcherTests.cs` suite as the regression net.

- [ ] **Step 1: Validate**

After Tasks 2-4, re-read the current shape of `DispatchAsync`. Locate where the pipeline begins (after `_consumeContextAccessor.Push`) and where it ends (just before the OCE catch). Confirm:
- `var hasResponseMessageId = headers.ContainsKey(HeaderKeys.ResponseMessageId)` is computed before the helper call (the pipeline needs it).
- The `replyProcessor` local is produced by `RunPreDeserializationProcessorsAsync` and consumed by `DispatchReplyAsync`.
- The `chain` build at `BuildProcessingChain(scope.ServiceProvider)` lives in the pipeline body.

- [ ] **Step 2: Add the helper**

Insert directly above `RunPreDeserializationProcessorsAsync` (line 338, before Task 3's helper if Task 3 placed `TryResolveMessageType` above it; otherwise above `TryResolveMessageType`). Choose the spot consistently with Tasks 3-4's placements.

```csharp
/// <summary>
/// Executes the dispatch pipeline for one inbound delivery: before-consuming filters,
/// pre-deserialization processors, reply-path dispatch (when applicable), main
/// deserialise + middleware chain + processors, and on-consumed-successfully filters.
/// Returns the <see cref="ConsumeEventResult"/> the caller surfaces; the after-consuming
/// filters and AsyncLocal pop run in the caller's finally block so they fire on the
/// exception paths too.
/// </summary>
private async Task<ConsumeEventResult> RunDispatchPipelineAsync(
    ReadOnlyMemory<byte> messageBytes,
    string messageType,
    IReadOnlyDictionary<string, object> headers,
    IDictionary<string, object> mutableHeaders,
    Envelope envelope,
    IServiceProvider scopedProvider,
    CancellationToken cancellationToken)
{
    // Before-consuming filters run first so they gate every dispatch path —
    // including pre-deserialization processors like StreamProcessor. Running
    // filters here guarantees stream packets and replies traverse the same
    // pre- and post-consume filter stages as any other message
    // (after-filters only fire once beforeFiltersRan is set).
    var beforeAction = await _filterPipeline.ExecuteBeforeConsumingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
    if (beforeAction == FilterAction.Stop)
    {
        return new ConsumeEventResult { Success = true };
    }

    var (replyProcessor, preDeserHandled) = await RunPreDeserializationProcessorsAsync(messageBytes, mutableHeaders, envelope, cancellationToken).ConfigureAwait(false);
    if (preDeserHandled)
    {
        // Mirror the reply branch and the handler-success branch: a pre-deserialisation
        // processor (StreamProcessor accepting a packet frame) that returns Handled is
        // a successful consume. User filters built on the OnConsumedSuccessfully stage
        // (dedup-key recording, audit, outbox commit) must observe stream packets here.
        await _filterPipeline.ExecuteOnConsumedSuccessfullyFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
        return new ConsumeEventResult { Success = true };
    }

    var hasResponseMessageId = headers.ContainsKey(HeaderKeys.ResponseMessageId);
    var typeResolution = TryResolveMessageType(messageType, headers, hasResponseMessageId);
    if (typeResolution.ShouldReturnNotHandled)
    {
        return new ConsumeEventResult { Success = true, NotHandled = true };
    }
    var type = typeResolution.Type!;

    if (hasResponseMessageId)
    {
        return await DispatchReplyAsync(replyProcessor, messageBytes, type, mutableHeaders, envelope, headers, cancellationToken).ConfigureAwait(false);
    }

    var message = _serializer.Deserialize(messageBytes, type);

    // Build the middleware chain per dispatch from the scoped provider so scoped/transient
    // middleware lifetimes are honoured — a cached chain would pin the first instance for
    // the lifetime of the bus.
    var chain = BuildProcessingChain(scopedProvider);
    var result = await chain(messageBytes, type, message, mutableHeaders, envelope, cancellationToken).ConfigureAwait(false);

    if (result.Success && !result.NotHandled)
    {
        await RunOnConsumedSuccessfullyAsync(envelope, messageType, cancellationToken).ConfigureAwait(false);
    }

    return result;
}
```

Note the helper computes `hasResponseMessageId` itself rather than receiving it as a parameter — it's a one-liner and keeps the parameter list manageable.

- [ ] **Step 3: Migrate `DispatchAsync`**

`DispatchAsync` collapses to:

```csharp
public async Task<ConsumeEventResult> DispatchAsync(
    ReadOnlyMemory<byte> messageBytes,
    string messageType,
    IReadOnlyDictionary<string, object> headers,
    CancellationToken cancellationToken = default)
{
    // CreateAsyncScope so user-supplied IMessageHandler / IFilter / IMessageProcessingMiddleware
    // implementations that are IAsyncDisposable-only (no IDisposable) are honoured. A sync scope
    // dispose against an IAsyncDisposable-only registered service throws
    // InvalidOperationException("AsyncDisposableServiceNotSupported") under MS.DI. Explicit
    // try/finally + DisposeAsync().ConfigureAwait(false) so the analyzer can see the await.
    var scope = _scopeFactory.CreateAsyncScope();
    try
    {
        using var _ = _scopeAccessor.Push(scope.ServiceProvider);

        Envelope? envelope = null;
        IDisposable? contextScope = null;
        var beforeFiltersRan = false;
        try
        {
            // Downstream pipeline (IMessageProcessor, MessageProcessingDelegate, Envelope.Headers)
            // requires a mutable IDictionary<string,object> for middleware mutation. Fast-path
            // succeeds when the runtime type is Dictionary<,> (the expected hot path); the fallback
            // copy handles non-Dictionary<,> runtime types (e.g. ReadOnlyDictionary<,>).
            var mutableHeaders = headers as IDictionary<string, object>
                ?? new Dictionary<string, object>(headers, StringComparer.Ordinal);

            envelope = new Envelope { Headers = mutableHeaders, Body = messageBytes };

            // Push the inbound-context accessor BEFORE filters/middleware run so any outbound
            // call made from a middleware (e.g. an auto-forward IMessageProcessingMiddleware
            // that invokes Bus.RouteAsync or Bus.SendAsync) reads the inbound hop counter via
            // ConsumeContextAccessor.CurrentHeaders.
            if (_consumeContextAccessor is not null)
            {
                var headersForContext = mutableHeaders as IReadOnlyDictionary<string, object>
                    ?? new Dictionary<string, object>(mutableHeaders, StringComparer.Ordinal);
                contextScope = _consumeContextAccessor.Push(headersForContext);
            }

            // Mark before-filters as having run BEFORE invoking the pipeline so the finally block
            // always invokes after-filters when the pipeline observed the envelope — including on
            // the exception paths handled by HandleDispatchErrorAsync.
            beforeFiltersRan = true;
            return await RunDispatchPipelineAsync(messageBytes, messageType, headers, mutableHeaders, envelope, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cooperative shutdown — propagate so the outer finally leaves the message unacked
            // for broker redelivery on next start. Not an application error.
            throw;
        }
        catch (Exception ex)
        {
            return await HandleDispatchErrorAsync(ex, messageType, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (beforeFiltersRan && envelope != null)
            {
                try
                {
                    await _filterPipeline.ExecuteAfterConsumingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception afterEx)
                {
                    _logger.LogWarning(afterEx, "AfterConsumingFilters threw while finalising dispatch of {MessageType}", messageType);
                }
            }
            // Pop the inbound-context AsyncLocal AFTER AfterConsumingFilters so those filters
            // still observe the headers context, but BEFORE the DI scope disposes so any
            // service depending on the accessor doesn't observe a stale push from this
            // dispatch in the next one.
            contextScope?.Dispose();
        }
    }
    finally
    {
        await scope.DisposeAsync().ConfigureAwait(false);
    }
}
```

**Critical correctness point — the `beforeFiltersRan` flag:** The original code sets `beforeFiltersRan = true` ONLY after the `ExecuteBeforeConsumingFiltersAsync` call succeeds (line 127). If that call throws, `beforeFiltersRan` stays false and the finally block does NOT invoke after-filters — correct, because the envelope was never fully observed by user filters.

In the refactor, `ExecuteBeforeConsumingFiltersAsync` runs INSIDE `RunDispatchPipelineAsync`. The caller doesn't know whether before-filters ran. **Set `beforeFiltersRan = true` BEFORE calling the helper** so the finally block always runs after-filters on the pipeline's exception paths. This is a slight semantic change: previously, an exception thrown from `ExecuteBeforeConsumingFiltersAsync` itself would have left `beforeFiltersRan == false`; now it always becomes `true`. Investigate whether this matters:

- If before-filters throws, the exception propagates up to the catch, which routes to `HandleDispatchErrorAsync`. The finally then runs after-filters — but the envelope was never seen by before-filters' user hooks. Is that a problem? Most user code installs symmetric before/after pairs (e.g. distributed-trace open/close) and expects after-filter to run only if before-filter ran.

**To preserve the exact original semantics**, pass `beforeFiltersRan` by ref into the helper, OR have the helper return a `(ConsumeEventResult, bool BeforeFiltersRan)` tuple. The simpler option: have the helper take `out bool beforeFiltersRan` and set it true once the before-filter call completes:

```csharp
private async Task<ConsumeEventResult> RunDispatchPipelineAsync(...)  // out param doesn't compose with async, so use a tuple
```

Better option: return a tuple. Update the signature:

```csharp
private async Task<(ConsumeEventResult Result, bool BeforeFiltersRan)> RunDispatchPipelineAsync(...)
{
    var beforeAction = await _filterPipeline.ExecuteBeforeConsumingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
    if (beforeAction == FilterAction.Stop)
    {
        return (new ConsumeEventResult { Success = true }, BeforeFiltersRan: true);
    }
    // ... rest of helper, all returning (result, BeforeFiltersRan: true)
}
```

And the caller:

```csharp
var pipelineOutcome = await RunDispatchPipelineAsync(...).ConfigureAwait(false);
beforeFiltersRan = pipelineOutcome.BeforeFiltersRan;
return pipelineOutcome.Result;
```

If the `await` faults (i.e. an exception thrown inside the helper), the assignment to `beforeFiltersRan` never happens — leaving it `false` if before-filters didn't complete, or false if it threw mid-call. Wait, this still loses the information: if before-filters returned Continue and then a later step threw, we want `beforeFiltersRan = true`.

The cleanest fix is: have the helper signal "before-filters completed" via a callback, OR via a shared mutable state object, OR have the helper set a member field. None of these are great.

**Decision: split the helper.** Run `ExecuteBeforeConsumingFiltersAsync` in `DispatchAsync` itself (one line), then call `RunDispatchPipelineAsync` for the body. This preserves the exact `beforeFiltersRan` semantic without contorting the helper's signature.

Revised plan for Step 2: place the before-filter call in `DispatchAsync` directly, then call the helper for everything after. Updated helper body:

```csharp
private async Task<ConsumeEventResult> RunDispatchPipelineAsync(
    ReadOnlyMemory<byte> messageBytes,
    string messageType,
    IReadOnlyDictionary<string, object> headers,
    IDictionary<string, object> mutableHeaders,
    Envelope envelope,
    IServiceProvider scopedProvider,
    CancellationToken cancellationToken)
{
    var (replyProcessor, preDeserHandled) = await RunPreDeserializationProcessorsAsync(messageBytes, mutableHeaders, envelope, cancellationToken).ConfigureAwait(false);
    if (preDeserHandled)
    {
        await _filterPipeline.ExecuteOnConsumedSuccessfullyFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
        return new ConsumeEventResult { Success = true };
    }

    var hasResponseMessageId = headers.ContainsKey(HeaderKeys.ResponseMessageId);
    var typeResolution = TryResolveMessageType(messageType, headers, hasResponseMessageId);
    if (typeResolution.ShouldReturnNotHandled)
    {
        return new ConsumeEventResult { Success = true, NotHandled = true };
    }
    var type = typeResolution.Type!;

    if (hasResponseMessageId)
    {
        return await DispatchReplyAsync(replyProcessor, messageBytes, type, mutableHeaders, envelope, headers, cancellationToken).ConfigureAwait(false);
    }

    var message = _serializer.Deserialize(messageBytes, type);
    var chain = BuildProcessingChain(scopedProvider);
    var result = await chain(messageBytes, type, message, mutableHeaders, envelope, cancellationToken).ConfigureAwait(false);

    if (result.Success && !result.NotHandled)
    {
        await RunOnConsumedSuccessfullyAsync(envelope, messageType, cancellationToken).ConfigureAwait(false);
    }

    return result;
}
```

And `DispatchAsync`'s inner try block becomes:

```csharp
// ... envelope construction, context push ...

// Before-consuming filters run first so they gate every dispatch path —
// including pre-deserialization processors like StreamProcessor. The
// beforeFiltersRan flag must reflect whether THIS call completed so the
// outer finally only invokes after-filters when before-filters were seen.
var beforeAction = await _filterPipeline.ExecuteBeforeConsumingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
beforeFiltersRan = true;
if (beforeAction == FilterAction.Stop)
{
    return new ConsumeEventResult { Success = true };
}

return await RunDispatchPipelineAsync(messageBytes, messageType, headers, mutableHeaders, envelope, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
```

This keeps the `beforeFiltersRan` semantic identical to the original.

- [ ] **Step 4: Build**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
```

- [ ] **Step 5: Run the MessageDispatcher tests + the surrounding Bus + Aggregator tests (broader regression net)**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageDispatcher|FullyQualifiedName~Bus" -m:1
```

Expected: all green. If `beforeFiltersRan` semantics drift (after-filters firing when they shouldn't, or vice versa), `BusTests` filter-pipeline tests + `MessageDispatcherTests` filter tests will catch it.

- [ ] **Step 6: Per-change code review**

`superpowers:requesting-code-review`. Highlight specifically:
- The `beforeFiltersRan` flag still tracks exactly when before-filters completed.
- After-filters fire only on the post-before-filter paths (including exception paths within the pipeline).
- The pipeline helper's responsibility boundary is documented in its xmldoc.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect/Services/MessageDispatcher.cs
git commit -m "$(cat <<'EOF'
refactor(dispatch): extract RunDispatchPipelineAsync coordinator

DispatchAsync now does: scope, envelope build, context push, before-
filter (kept in caller so beforeFiltersRan tracks correctly), then
delegates the pipeline body — pre-deserialisation processors, type
resolution, reply branch, deserialise+chain+dispatch, on-consumed-
successfully — to RunDispatchPipelineAsync. The OCE re-throw, error
classification (via HandleDispatchErrorAsync), and after-filter finally
stay in the caller. ~100 LOC removed from DispatchAsync; total file
roughly unchanged because the helpers absorb the inline body.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: End-of-phase verification gate

Same shape as Phase 1 and Phase 2 end-of-phase gates. Each step is a delegated subagent.

- [ ] **Step 1: Final branch-wide code review**

Dispatch via `superpowers:requesting-code-review` on the diff `<phase-3c-base>..HEAD`. The base is the commit immediately before Task 1's run (Phase 3c started after Phase 2's `87bf13e0`). Subagent confirms behaviour preservation, no public API drift, no warning suppressions, no surprise dead-code-removal beyond the documented one at original line 171-176.

- [ ] **Step 2: Full unit-test suite**

Delegated subagent: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1`. Phase 2 baseline 1644 — Phase 3c adds zero new tests (refactor only), so expected 1644 still.

- [ ] **Step 3: Full end-to-end suite**

Delegated subagent: `dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`. Expected 136/136.

- [ ] **Step 4: Serialization-compat tests**

Delegated subagent: `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj -m:1`. Expected 48/48.

- [ ] **Step 5: All example apps build**

Delegated subagent: `find examples -name '*.csproj' -print0 | xargs -0 -I{} dotnet build "{}" -m:1 --nologo --verbosity quiet`. Expected 53/53 clean.

- [ ] **Step 6: Documentation site + README currency**

Delegated subagent: scan `website/src/content/docs/` and `README.md` for any reference to `DispatchAsync`'s internal shape that would now be stale. Phase 3c is pure internal refactor; expected outcome: NO UPDATE NEEDED.

- [ ] **Step 7: LOC sanity check**

```bash
wc -l src/ServiceConnect/Services/MessageDispatcher.cs
```

Compare to Task 1's baseline (~416). Expected: roughly unchanged. The helpers absorb the inline body, so the file totals are similar; what changes is the per-method size.

```bash
# DispatchAsync after Task 5 should be ~50-70 lines vs. ~200 before
sed -n '/public async Task<ConsumeEventResult> DispatchAsync/,/^    \(public\|private\) /p' src/ServiceConnect/Services/MessageDispatcher.cs | wc -l
```

Record the actual count.

- [ ] **Step 8: Close Phase 3c**

When all 7 gate steps PASS, update the controller's TodoWrite list to mark Phase 3c complete. No additional commit — the phase is closed by passing the gate.

---

## Self-Review Checklist

1. **Spec coverage:**
   - Plan's design decision 1 (TryResolveMessageType): ✔ Task 3.
   - Plan's design decision 2 (RunDispatchPipelineAsync): ✔ Task 5.
   - Plan's design decision 3 (HandleDispatchErrorAsync, not the OCE): ✔ Task 4.
   - Plan's design decision 4 (rename single-letter params): ✔ Task 2.
   - Plan's design decision 5 (existing tests are the regression net, no new tests): ✔ Implicit in every task — no test file is modified, only the existing `MessageDispatcherTests.cs` runs each step.
   - **Surfaced during planning:** the `var next = chain` review concern is technically incorrect; documented at the head of Task 2 as deliberately NOT renamed.
   - **Surfaced during planning:** the dead-code branch at original lines 171-176; documented at the head of Task 1 step 4 and rolled into Task 3's commit.

2. **Placeholder scan:** Every step has real code or a real command. No "TODO", no "implement later".

3. **Type consistency:**
   - `MessageTypeResolution(Type? Type, bool ShouldReturnNotHandled)` — used in Task 3 and consumed in Task 5.
   - `HandleDispatchErrorAsync(Exception, string, CancellationToken)` — used in Task 4 and consumed in Task 5.
   - `RunDispatchPipelineAsync(messageBytes, messageType, headers, mutableHeaders, envelope, scopedProvider, cancellationToken)` — defined in Task 5; matches the call site in `DispatchAsync`.

4. **Test patterns:** No new tests. Regression net is `MessageDispatcherTests.cs` (32+ `[Fact]` tests, 1114 lines) plus the broader Bus + Aggregator suites for Task 5's wider blast radius.

5. **Dotnet delegation:** Every `dotnet` step is invoked from the implementer subagent — main session never invokes dotnet.

6. **Commit hygiene:** Each task = one commit. Scoped prefix, present-tense summary, Claude co-author trailer. No ticket / phase / "fixes Xxx" framing in the source code (the plan file is documentation).

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-05-18-phase-3c-messagedispatcher.md`. Two execution options:

**1. Subagent-Driven (recommended)** — fresh implementer subagent per task + spec-compliance review + code-quality review per task. Same workflow that closed Phases 1 and 2 cleanly.

**2. Inline Execution** — `superpowers:executing-plans` with checkpoints.

Which approach?
