# Phase A.2 — Serializer + body-type cascade Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Newtonsoft.Json with System.Text.Json across ServiceConnect's shipped packages, redesign `IMessageSerializer` to a 3-method zero-copy contract, complete the body-type cascade through `SendContext.MessageBytes` and `IProducer`, and lock in v7↔v8 wire compatibility via a dedicated cross-impl round-trip test corpus.

**Architecture:** This is the second of three phases delivering Group A of the v8 public-API tightening. Phase A.1 landed the foundations (`IHasCorrelationId`, nullable `TimeoutData.Destination`, aggregator persistor cleanup). Phase A.2 is the biggest commit window of Group A: the JSON serializer engine swaps from Newtonsoft to STJ, the `byte[]` payload cascade through `Bus → SendContext → Producer` becomes `ReadOnlyMemory<byte>`, and a new `SerializationCompatTests` project guards mixed-version deployments by round-tripping a representative corpus of message types through both serializer engines. Phase A.3 will follow with the remaining surface (`IBus.SendToManyAsync`, `IConsumer`, `IMessageDispatcher`, `IConsumeContext`, `ReplyOptions`).

**Tech Stack:** .NET 8/10, C# 12/14, System.Text.Json (BCL), Newtonsoft.Json 13 (test-only after this phase), xUnit, MongoDB.Driver. Dotnet wrapper at `~/.local/bin/dotnet` enforces cgroup limits (CPUQuota=800%, MemoryMax=8G, TasksMax=200) — call `dotnet` normally; do NOT use `/usr/lib/dotnet/dotnet` directly.

**Key v8 invariants:** `TreatWarningsAsErrors=true`, `Nullable=enable`, `EnforceCodeStyleInBuild=true`, `GenerateDocumentationFile=true`. Analyzers: Meziantou + VS Threading + NetAnalyzers + IDE0xxx via .editorconfig.

**Lessons from Phase A.1 baked into this plan:**
1. Every pre-commit verification step runs `dotnet build src/ServiceConnect.slnx -m:1` (full solution, serial), not just per-project. Avoids the EndToEndTests-collateral surprise of Phase A.1.
2. Each task that retypes a collection or buffer includes a "variance audit" step: grep for `as IList<`, `as IDictionary<`, `as Span<`, `as Memory<` in touched files; verify each cast still has a chance of succeeding under the new types. The dead `as IList<object>` we hit in A.1 cost a follow-up commit.
3. Wire-compat is non-negotiable: the corpus tests exist to enforce JSON-equivalence between Newtonsoft (v7 reference behaviour) and STJ (v8 production), so v7↔v8 mixed deployments survive the swap.

---

## File Structure

**New project:**
- `src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj` — xUnit test project. References Newtonsoft.Json 13 (test-only) and the production `ServiceConnect` assembly. Provides an isolated home for the round-trip corpus.

**New files (production):**
- `src/ServiceConnect/Services/SystemTextJsonMessageSerializer.cs` — STJ-backed `IMessageSerializer` implementation. Mirrors the wire-format defaults of `NewtonsoftJsonMessageSerializer.cs` (relaxed Unicode escaping, ISO 8601 round-trip dates, `MaxDepth = 32`). After Task 6, this is the only serializer shipped with ServiceConnect.

**New files (test fixtures + corpus):**
- `src/ServiceConnect.SerializationCompatTests/Fixtures/NewtonsoftReferenceSerializer.cs` — a self-contained reference impl that reproduces the v7 wire format. Does NOT implement `IMessageSerializer` (decoupled from the production interface) so it survives the interface reduction in Task 6.
- `src/ServiceConnect.SerializationCompatTests/Corpus/CorpusTypes.cs` — ~10 representative `Message`-derived types (primitives, collections, nested objects, dates, enums, base64 byte fields). Public so xUnit theory data can iterate them.
- `src/ServiceConnect.SerializationCompatTests/Corpus/CorpusFactory.cs` — builds populated instances of each corpus type for the round-trip tests.
- `src/ServiceConnect.SerializationCompatTests/RoundTripTests.cs` — the four assertions described in the spec: STJ↔STJ structural equality, Newtonsoft→STJ deserialise, STJ→Newtonsoft deserialise, and JSON-DOM equivalence between Newtonsoft and STJ outputs.
- `src/ServiceConnect.SerializationCompatTests/PathologicalInputTests.cs` — deeply-nested input (≥30 levels), large strings, NaN/Infinity rejection, mixed-case property names.

**Files modified (production):**
- `src/ServiceConnect.Interfaces/Messages/IMessageSerializer.cs` — reduced from 9 methods to 3 (Task 6).
- `src/ServiceConnect.Interfaces/Pipelines/SendContext.cs` — `MessageBytes` typed as `ReadOnlyMemory<byte>` (init-only).
- `src/ServiceConnect.Interfaces/Bus/IProducer.cs` — all four methods retyped: body parameters become `ReadOnlyMemory<byte>`, headers parameters become `IReadOnlyDictionary<string,string>?`.
- `src/ServiceConnect/Bus.cs` — Serialize call sites switch to `ArrayBufferWriter<byte>`; `SendContext.MessageBytes` constructed from `WrittenMemory`.
- `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` — body parameters retyped; the `(ReadOnlyMemory<byte>)message` casts at the four publish sites delete (already `ROM<byte>` end-to-end).
- `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs` — `BuildHeaders`'s `headers` parameter tightened from `IDictionary<string,string>?` to `IReadOnlyDictionary<string,string>?`.

**Files modified (csproj):**
- `src/ServiceConnect/ServiceConnect.csproj` — `<PackageReference Include="Newtonsoft.Json" />` removed (Task 7).
- `src/ServiceConnect.slnx` — register the new `ServiceConnect.SerializationCompatTests` project (Task 2).

**Files deleted (production):**
- `src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs` — superseded by STJ impl (Task 6).
- `src/ServiceConnect/Services/IO/ReadOnlyMemoryStream.cs`, `src/ServiceConnect/Services/IO/ReadOnlySequenceStream.cs` — used today by Newtonsoft `Deserialize(ROM<byte>, Type)` and `Deserialize(ROS<byte>, Type)` to wrap as `Stream`. STJ has native span-reading via `Utf8JsonReader`; these wrappers become unused. Verify no other consumer remains before deleting in Task 7.

**Out of scope for this phase:**
- `IBus.SendToManyAsync`, `RouteAsync` tightening, `SendOptions.EndPoints` removal — Phase A.3.
- `IConsumer.StartConsumingAsync` messageTypes tightening — Phase A.3.
- `IMessageDispatcher.DispatchAsync` headers tightening — Phase A.3.
- `IConsumeContext.ReplyAsync` + `ReplyOptions` — Phase A.3.
- Examples updates (`examples/**`) — Phase A.3.
- `Envelope.Body` change — already `ReadOnlyMemory<byte>` in the codebase; nothing to do.

---

## Variance audit candidates

After every retyping commit (Tasks 6 and 7), grep the diffed files for these patterns and confirm each `as` cast still has a chance of succeeding under the new types:

```bash
grep -nE 'as IList<|as ICollection<|as IDictionary<|as IReadOnlyList<|as IReadOnlyCollection<|as Span<|as Memory<|as ReadOnlySpan<|as ReadOnlyMemory<' \
  src/ServiceConnect/Bus.cs \
  src/ServiceConnect/Services/*.cs \
  src/ServiceConnect.Client.RabbitMQ/Producer/*.cs
```

Expected: no hits in the diff range, or every hit is verifiably live under the new types. (Phase A.1 hit a dead `as IList<object>` against `IReadOnlyList<IHasCorrelationId>` — variance was the trap.)

---

## Task 1: Pre-flight check (no code change)

This task is **read-only verification** that the pre-flight already done is consistent with the current branch state. Spec already corrected in commit `e79217dc`.

- [ ] **Step 1: Confirm spec is at expected SHA**

Run: `cd /home/tim/source/ServiceConnect-CSharp && git log --oneline -1 docs/superpowers/specs/2026-05-03-v8-public-api-tightening-design.md`
Expected: `e79217dc docs(spec): correct body-type cascade section against actual code` (or later if further drift was caught).

- [ ] **Step 2: Confirm Envelope.Body and SendContext.MessageBytes states match plan assumptions**

Run: `grep -n 'Body\|MessageBytes' src/ServiceConnect.Interfaces/Pipelines/SendContext.cs src/ServiceConnect.Interfaces/Messages/Envelope.cs`
Expected:
- `SendContext.cs:17: public required byte[] MessageBytes { get; init; }`  ← changes in Task 6
- `Envelope.cs:17: public ReadOnlyMemory<byte> Body { get; init; } = ReadOnlyMemory<byte>.Empty;`  ← already done

- [ ] **Step 3: Confirm IProducer body parameter sites**

Run: `grep -nE 'byte\[\] (message|body|packet)' src/ServiceConnect.Interfaces/Bus/IProducer.cs`
Expected: 4 hits at lines 11, 16, 21, 28 (PublishAsync, two SendAsync overloads, SendBytesAsync).

No commit for this task — it's a pre-flight audit.

---

## Task 2: Create SerializationCompatTests project skeleton

**Files:**
- Create: `src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj`
- Modify: `src/ServiceConnect.slnx`

- [ ] **Step 1: Create the new csproj**

Create `src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <!-- Test project: do not produce a NuGet package. -->
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ServiceConnect\ServiceConnect.csproj" />
    <ProjectReference Include="..\ServiceConnect.Interfaces\ServiceConnect.Interfaces.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- Test infrastructure -->
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />

    <!-- Newtonsoft is referenced only here, scoped to wire-compat verification.
         Production packages have no Newtonsoft dependency after Phase A.2. -->
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
```

Cross-check the test-package versions against the existing `src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`. Use whatever versions that file uses for `Microsoft.NET.Test.Sdk`, `xunit`, and `xunit.runner.visualstudio` — keeping all test projects on the same versions avoids transitive-dependency surprises.

```bash
grep -E 'Microsoft\.NET\.Test\.Sdk|xunit' src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
```

Replace the version strings above with whatever the UnitTests project pins.

- [ ] **Step 2: Add the project to the solution**

Run: `dotnet sln src/ServiceConnect.slnx add src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj`

This rewrites `src/ServiceConnect.slnx` to register the new project. Verify the diff with `git diff src/ServiceConnect.slnx`.

- [ ] **Step 3: Build the new project to verify the csproj is well-formed**

Run: `dotnet build src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj`
Expected: 0 errors, 0 warnings. (The project is empty; the build only verifies dependencies resolve and the csproj parses.)

- [ ] **Step 4: Full solution build (per Phase A.1 lesson #1)**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings across the solution. The `-m:1` serializes project builds to dodge the MSBuild Copy-task OOM that Phase A.1 hit on this machine.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj src/ServiceConnect.slnx
git commit -m "$(cat <<'EOF'
test(serialization-compat): add SerializationCompatTests project skeleton

New xUnit test project that will host the v7↔v8 wire-compat round-trip
corpus. Newtonsoft.Json 13 is referenced only here, scoped to test
verification. After Phase A.2, no production package references
Newtonsoft.

Empty for now — Tasks 3-5 add the reference fixture, corpus types, and
round-trip tests; Task 6 swaps the production serializer to STJ behind
the safety net these tests provide.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Newtonsoft reference fixture

A self-contained class that reproduces the v7 production wire format using Newtonsoft.Json. Lives in the test project; does NOT implement `IMessageSerializer` (intentionally decoupled — the production interface will reduce in Task 6, and this fixture must survive that reduction unchanged).

**Files:**
- Create: `src/ServiceConnect.SerializationCompatTests/Fixtures/NewtonsoftReferenceSerializer.cs`

- [ ] **Step 1: Create the fixture**

Create `src/ServiceConnect.SerializationCompatTests/Fixtures/NewtonsoftReferenceSerializer.cs`:

```csharp
using Newtonsoft.Json;
using ServiceConnect.Interfaces;
using System.Text;

namespace ServiceConnect.SerializationCompatTests.Fixtures;

/// <summary>
/// Reference v7 wire-format serializer — Newtonsoft.Json with the exact settings the
/// production NewtonsoftJsonMessageSerializer used pre-Phase-A.2. Decoupled from
/// <see cref="IMessageSerializer"/> by design: the production interface reduces in
/// Phase A.2 Task 6, and this fixture must keep representing v7 behaviour after that.
/// </summary>
internal static class NewtonsoftReferenceSerializer
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        NullValueHandling = NullValueHandling.Include,
        DefaultValueHandling = DefaultValueHandling.Include,
        ReferenceLoopHandling = ReferenceLoopHandling.Error,
        DateFormatHandling = DateFormatHandling.IsoDateFormat,
        DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind,
        Formatting = Formatting.None,
        TypeNameHandling = TypeNameHandling.None,
    };

    private static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

    public static byte[] Serialize<T>(T message) where T : Message
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        using var ms = new MemoryStream();
        using (var sw = new StreamWriter(ms, Encoding.UTF8, bufferSize: 1024, leaveOpen: true))
        using (var jw = new JsonTextWriter(sw))
        {
            Serializer.Serialize(jw, message);
        }
        return ms.ToArray();
    }

    public static object Deserialize(byte[] data, Type type)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var sr = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: false);
        using var jr = new JsonTextReader(sr);
        return Serializer.Deserialize(jr, type)
            ?? throw new InvalidOperationException($"Newtonsoft reference deserialised to null for {type.Name}");
    }

    public static T Deserialize<T>(byte[] data) where T : Message
        => (T)Deserialize(data, typeof(T));
}
```

- [ ] **Step 2: Build the test project**

Run: `dotnet build src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj`
Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.SerializationCompatTests/Fixtures/NewtonsoftReferenceSerializer.cs
git commit -m "$(cat <<'EOF'
test(serialization-compat): add Newtonsoft reference fixture

NewtonsoftReferenceSerializer reproduces the v7 production wire format
with the exact settings NewtonsoftJsonMessageSerializer used:
TypeNameHandling.None, ISO 8601 dates with RoundtripKind, NullValueHandling.Include,
ReferenceLoopHandling.Error.

Decoupled from IMessageSerializer by design — the production interface
reduces from 9 methods to 3 in Phase A.2 Task 6, and this fixture must
keep representing v7 wire behaviour after that. The fixture exposes
plain Serialize<T>/Deserialize<T> static methods that the round-trip
corpus drives directly.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Add SystemTextJsonMessageSerializer to production (still alongside Newtonsoft)

Adds the new STJ-backed implementation. Implements the **current 9-method `IMessageSerializer`** so it can be wired into DI and exercised by the corpus tests. Task 6 reduces both the interface and this impl to 3 methods atomically.

**Files:**
- Create: `src/ServiceConnect/Services/SystemTextJsonMessageSerializer.cs`

- [ ] **Step 1: Create the STJ implementation**

Create `src/ServiceConnect/Services/SystemTextJsonMessageSerializer.cs`:

```csharp
using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Services;

/// <summary>
/// System.Text.Json implementation of <see cref="IMessageSerializer"/>. Wire format
/// is JSON-equivalent to the v7 Newtonsoft implementation under the matching settings
/// (relaxed Unicode escaping, ISO 8601 round-trip dates, MaxDepth = 32). Cross-version
/// behaviour is enforced by the SerializationCompatTests corpus.
/// </summary>
public sealed class SystemTextJsonMessageSerializer : IMessageSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Creates a serializer using optionally-customised STJ options.
    /// </summary>
    /// <param name="options">Optional source options to clone and apply. The cloned
    /// instance has ServiceConnect's wire-compat defaults applied unless the source
    /// already supplied a value.</param>
    public SystemTextJsonMessageSerializer(JsonSerializerOptions? options = null)
    {
        _options = new JsonSerializerOptions(options ?? new JsonSerializerOptions())
        {
            // Match HeaderDecoder.MaxDepth = 32 cap on inbound nesting.
            MaxDepth = 32,

            // Newtonsoft's default emits literal non-ASCII characters; STJ default
            // escapes them. UnsafeRelaxedJsonEscaping keeps the wire bytes byte-identical
            // for typical payloads, which is what the corpus tests assert.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

            // Newtonsoft.Json tolerates string-encoded numbers ("3" → int) by default.
            // Match that behaviour so a v7 producer's payload deserialises here.
            NumberHandling = JsonNumberHandling.AllowReadingFromString,

            // Newtonsoft default is case-sensitive matching; preserve.
            PropertyNameCaseInsensitive = false,

            // Newtonsoft serialises only properties (not fields) by default; preserve.
            IncludeFields = false,

            // Equivalent of NullValueHandling.Include — emit null fields on the wire.
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
    }

    /// <inheritdoc />
    public byte[] Serialize<T>(T message) where T : Message
    {
        if (message is null)
        {
            throw new SerializationException("Cannot serialize null message", typeof(T));
        }

        try
        {
            var writer = new ArrayBufferWriter<byte>();
            using (var jsonWriter = new Utf8JsonWriter(writer))
            {
                JsonSerializer.Serialize(jsonWriter, message, message.GetType(), _options);
            }
            return writer.WrittenSpan.ToArray();
        }
        catch (JsonException ex)
        {
            throw new SerializationException($"Failed to serialize message of type {typeof(T).Name}", typeof(T), ex);
        }
    }

    /// <inheritdoc />
    public void Serialize<T>(T message, IBufferWriter<byte> output) where T : Message
    {
        ArgumentNullException.ThrowIfNull(output);
        if (message is null)
        {
            throw new SerializationException("Cannot serialize null message", typeof(T));
        }

        try
        {
            using var jsonWriter = new Utf8JsonWriter(output);
            JsonSerializer.Serialize(jsonWriter, message, message.GetType(), _options);
        }
        catch (JsonException ex)
        {
            throw new SerializationException($"Failed to serialize message of type {typeof(T).Name}", typeof(T), ex);
        }
    }

    /// <inheritdoc />
    public T Deserialize<T>(byte[] data) where T : Message
        => (T)Deserialize(data, typeof(T));

    /// <inheritdoc />
    public T Deserialize<T>(ReadOnlySpan<byte> data) where T : Message
        => (T)Deserialize(data, typeof(T));

    /// <inheritdoc />
    public object Deserialize(byte[] data, Type type)
        => Deserialize((ReadOnlySpan<byte>)data, type);

    /// <inheritdoc />
    public object Deserialize(ReadOnlySpan<byte> data, Type type)
    {
        try
        {
            return JsonSerializer.Deserialize(data, type, _options)
                ?? throw new SerializationException(
                    $"Deserialization returned null for type {type.Name}", type);
        }
        catch (JsonException ex)
        {
            throw new SerializationException(
                $"Failed to deserialize message of type {type.Name}", type, ex);
        }
    }

    /// <inheritdoc />
    public object Deserialize(ReadOnlyMemory<byte> data, Type type)
        => Deserialize(data.Span, type);

    /// <inheritdoc />
    public T Deserialize<T>(ReadOnlyMemory<byte> data) where T : Message
        => (T)Deserialize(data.Span, typeof(T));

    /// <inheritdoc />
    public object Deserialize(in ReadOnlySequence<byte> data, Type type)
    {
        try
        {
            var reader = new Utf8JsonReader(data, isFinalBlock: true, state: default);
            return JsonSerializer.Deserialize(ref reader, type, _options)
                ?? throw new SerializationException(
                    $"Deserialization returned null for type {type.Name}", type);
        }
        catch (JsonException ex)
        {
            throw new SerializationException(
                $"Failed to deserialize message of type {type.Name}", type, ex);
        }
    }
}
```

- [ ] **Step 2: Build the production project**

Run: `dotnet build src/ServiceConnect/ServiceConnect.csproj`
Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings. The DI registration is unchanged — production still uses `NewtonsoftJsonMessageSerializer`. STJ impl is now resolvable but not wired.

- [ ] **Step 4: Run unit tests as a regression guard**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore`
Expected: all tests pass (no behavioural change yet).

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/SystemTextJsonMessageSerializer.cs
git commit -m "$(cat <<'EOF'
feat(services): add SystemTextJsonMessageSerializer alongside Newtonsoft

SystemTextJsonMessageSerializer implements the current 9-method
IMessageSerializer interface using System.Text.Json. Wire-compat with
v7 Newtonsoft is achieved via:

- MaxDepth = 32 (matches HeaderDecoder cap)
- JavaScriptEncoder.UnsafeRelaxedJsonEscaping (literal Unicode like Newtonsoft)
- NumberHandling.AllowReadingFromString (Newtonsoft tolerates "3" -> int)
- PropertyNameCaseInsensitive = false; IncludeFields = false
- DefaultIgnoreCondition = Never (matches NullValueHandling.Include)

Production DI registration is unchanged in this commit — Newtonsoft is
still the active serializer. The next two tasks build the wire-compat
test corpus that exercises both impls; Task 6 then atomically swaps DI
and reduces the IMessageSerializer interface to 3 methods.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Wire-compat corpus + round-trip tests

Adds representative corpus types and the four round-trip assertions described in the spec.

**Files:**
- Create: `src/ServiceConnect.SerializationCompatTests/Corpus/CorpusTypes.cs`
- Create: `src/ServiceConnect.SerializationCompatTests/Corpus/CorpusFactory.cs`
- Create: `src/ServiceConnect.SerializationCompatTests/RoundTripTests.cs`
- Create: `src/ServiceConnect.SerializationCompatTests/PathologicalInputTests.cs`

- [ ] **Step 1: Define corpus types**

Create `src/ServiceConnect.SerializationCompatTests/Corpus/CorpusTypes.cs`:

```csharp
using ServiceConnect.Interfaces;

namespace ServiceConnect.SerializationCompatTests.Corpus;

// ---- Primitives ----

public sealed class PrimitiveMessage : Message
{
    public PrimitiveMessage() : base(Guid.Empty) { }
    public PrimitiveMessage(Guid correlationId) : base(correlationId) { }

    public int Int32 { get; init; }
    public long Int64 { get; init; }
    public double Double { get; init; }
    public decimal Decimal { get; init; }
    public bool Bool { get; init; }
    public string String { get; init; } = "";
    public Guid Guid { get; init; }
}

// ---- Collections ----

public sealed class CollectionMessage : Message
{
    public CollectionMessage() : base(Guid.Empty) { }
    public CollectionMessage(Guid correlationId) : base(correlationId) { }

    public List<int> IntList { get; init; } = [];
    public Dictionary<string, string> StringDict { get; init; } = [];
    public string[] StringArray { get; init; } = [];
}

// ---- Nullable fields ----

public sealed class NullableMessage : Message
{
    public NullableMessage() : base(Guid.Empty) { }
    public NullableMessage(Guid correlationId) : base(correlationId) { }

    public int? NullableInt { get; init; }
    public string? NullableString { get; init; }
    public DateTime? NullableDateTime { get; init; }
}

// ---- Nested objects ----

public sealed class NestedMessage : Message
{
    public NestedMessage() : base(Guid.Empty) { }
    public NestedMessage(Guid correlationId) : base(correlationId) { }

    public Inner Child { get; init; } = new();

    public sealed class Inner
    {
        public string Name { get; init; } = "";
        public Inner? Grandchild { get; init; }
    }
}

// ---- Date / time variants ----

public sealed class DateTimeMessage : Message
{
    public DateTimeMessage() : base(Guid.Empty) { }
    public DateTimeMessage(Guid correlationId) : base(correlationId) { }

    public DateTime UtcKind { get; init; }
    public DateTime LocalKind { get; init; }
    public DateTime UnspecifiedKind { get; init; }
    public DateTimeOffset Offset { get; init; }
    public TimeSpan Duration { get; init; }
}

// ---- Enums ----

public enum CorpusEnum
{
    First = 0,
    Second = 1,
    Third = 2,
}

public sealed class EnumMessage : Message
{
    public EnumMessage() : base(Guid.Empty) { }
    public EnumMessage(Guid correlationId) : base(correlationId) { }

    public CorpusEnum Value { get; init; }
}

// ---- byte[] payload ----

public sealed class ByteArrayMessage : Message
{
    public ByteArrayMessage() : base(Guid.Empty) { }
    public ByteArrayMessage(Guid correlationId) : base(correlationId) { }

    public byte[] Payload { get; init; } = [];
}

// ---- Polymorphism via base + derived (no $type metadata) ----

public abstract class Animal
{
    public string Name { get; init; } = "";
}

public sealed class Dog : Animal
{
    public string Breed { get; init; } = "";
}

public sealed class PolymorphicMessage : Message
{
    public PolymorphicMessage() : base(Guid.Empty) { }
    public PolymorphicMessage(Guid correlationId) : base(correlationId) { }

    public Dog Pet { get; init; } = new();
}

// ---- Empty message (CorrelationId only) ----

public sealed class EmptyMessage : Message
{
    public EmptyMessage() : base(Guid.Empty) { }
    public EmptyMessage(Guid correlationId) : base(correlationId) { }
}
```

- [ ] **Step 2: Create the corpus factory**

Create `src/ServiceConnect.SerializationCompatTests/Corpus/CorpusFactory.cs`:

```csharp
using ServiceConnect.Interfaces;

namespace ServiceConnect.SerializationCompatTests.Corpus;

/// <summary>
/// Produces populated instances of every <see cref="Message"/> subtype defined in
/// <see cref="CorpusTypes"/>. xUnit theory data drives every corpus item through the
/// four round-trip assertions in <c>RoundTripTests</c>.
/// </summary>
public static class CorpusFactory
{
    private static readonly Guid TestCorrelationId = Guid.Parse("00000000-0000-0000-0000-000000000042");

    public static IEnumerable<object[]> AllCorpusItems()
    {
        yield return [Primitive()];
        yield return [Collection()];
        yield return [Nullable()];
        yield return [Nested()];
        yield return [Dates()];
        yield return [Enum()];
        yield return [ByteArray()];
        yield return [Polymorphic()];
        yield return [Empty()];
    }

    public static PrimitiveMessage Primitive() => new(TestCorrelationId)
    {
        Int32 = 42,
        Int64 = 9_000_000_000L,
        Double = 3.14159,
        Decimal = 12345.6789m,
        Bool = true,
        String = "hello — utf8 ✓",
        Guid = Guid.Parse("11111111-2222-3333-4444-555555555555"),
    };

    public static CollectionMessage Collection() => new(TestCorrelationId)
    {
        IntList = [1, 2, 3, 4, 5],
        StringDict = new Dictionary<string, string> { ["k1"] = "v1", ["k2"] = "v2" },
        StringArray = ["a", "b", "c"],
    };

    public static NullableMessage Nullable() => new(TestCorrelationId)
    {
        NullableInt = null,
        NullableString = null,
        NullableDateTime = null,
    };

    public static NestedMessage Nested() => new(TestCorrelationId)
    {
        Child = new NestedMessage.Inner
        {
            Name = "child",
            Grandchild = new NestedMessage.Inner { Name = "grandchild", Grandchild = null },
        },
    };

    public static DateTimeMessage Dates() => new(TestCorrelationId)
    {
        UtcKind = new DateTime(2026, 5, 3, 12, 0, 0, DateTimeKind.Utc),
        LocalKind = new DateTime(2026, 5, 3, 12, 0, 0, DateTimeKind.Local),
        UnspecifiedKind = new DateTime(2026, 5, 3, 12, 0, 0, DateTimeKind.Unspecified),
        Offset = new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.FromHours(1)),
        Duration = TimeSpan.FromMinutes(90),
    };

    public static EnumMessage Enum() => new(TestCorrelationId) { Value = CorpusEnum.Second };

    public static ByteArrayMessage ByteArray() => new(TestCorrelationId)
    {
        Payload = [0x01, 0x02, 0x03, 0xff, 0xfe, 0xfd],
    };

    public static PolymorphicMessage Polymorphic() => new(TestCorrelationId)
    {
        Pet = new Dog { Name = "Rex", Breed = "Border Collie" },
    };

    public static EmptyMessage Empty() => new(TestCorrelationId);
}
```

- [ ] **Step 3: Create the round-trip tests**

Create `src/ServiceConnect.SerializationCompatTests/RoundTripTests.cs`:

```csharp
using System.Text.Json;
using Newtonsoft.Json.Linq;
using ServiceConnect.Interfaces;
using ServiceConnect.SerializationCompatTests.Corpus;
using ServiceConnect.SerializationCompatTests.Fixtures;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.SerializationCompatTests;

/// <summary>
/// Cross-impl wire-compat assertions. Each corpus item is round-tripped through four
/// channels:
///   1. STJ serialize → STJ deserialize: structural equality (control).
///   2. Newtonsoft serialize → STJ deserialize: v7 producer to v8 consumer.
///   3. STJ serialize → Newtonsoft deserialize: v8 producer to v7 consumer.
///   4. Wire-byte JSON-DOM equivalence: STJ output and Newtonsoft output parse to the
///      same JSON document. (Bytes may differ in escape sequences; meaning is identical.)
/// </summary>
public class RoundTripTests
{
    private static readonly SystemTextJsonMessageSerializer Stj = new();

    [Theory]
    [MemberData(nameof(CorpusFactory.AllCorpusItems), MemberType = typeof(CorpusFactory))]
    public void Stj_ToStj_RoundTrip_StructurallyEqual(Message message)
    {
        var bytes = Stj.Serialize(message);
        var deserialised = Stj.Deserialize(bytes, message.GetType());
        AssertStructurallyEqual(message, deserialised);
    }

    [Theory]
    [MemberData(nameof(CorpusFactory.AllCorpusItems), MemberType = typeof(CorpusFactory))]
    public void Newtonsoft_ToStj_DeserialiseSucceeds(Message message)
    {
        var bytes = NewtonsoftReferenceSerializer.Serialize(message);
        var deserialised = Stj.Deserialize(bytes, message.GetType());
        AssertStructurallyEqual(message, deserialised);
    }

    [Theory]
    [MemberData(nameof(CorpusFactory.AllCorpusItems), MemberType = typeof(CorpusFactory))]
    public void Stj_ToNewtonsoft_DeserialiseSucceeds(Message message)
    {
        var bytes = Stj.Serialize(message);
        var deserialised = NewtonsoftReferenceSerializer.Deserialize(bytes, message.GetType());
        AssertStructurallyEqual(message, deserialised);
    }

    [Theory]
    [MemberData(nameof(CorpusFactory.AllCorpusItems), MemberType = typeof(CorpusFactory))]
    public void Stj_And_Newtonsoft_Outputs_AreJsonEquivalent(Message message)
    {
        var stjBytes = Stj.Serialize(message);
        var newtonsoftBytes = NewtonsoftReferenceSerializer.Serialize(message);

        // Bytes may differ (Unicode escape choices, whitespace) but the JSON DOMs must match.
        var stjDocument = JsonDocument.Parse(stjBytes);
        var newtonsoftDocument = JsonDocument.Parse(newtonsoftBytes);

        Assert.True(JsonElementsEqual(stjDocument.RootElement, newtonsoftDocument.RootElement),
            $"STJ output and Newtonsoft output are not JSON-DOM equivalent.\n" +
            $"STJ: {System.Text.Encoding.UTF8.GetString(stjBytes)}\n" +
            $"Newtonsoft: {System.Text.Encoding.UTF8.GetString(newtonsoftBytes)}");
    }

    /// <summary>
    /// Structural equality asserted via Newtonsoft's <see cref="JToken.DeepEquals(JToken, JToken)"/>:
    /// both ends of the round-trip serialise to the same DOM via Newtonsoft. This intentionally
    /// uses the reference impl (Newtonsoft) on both sides so the assertion is independent of STJ —
    /// asserting "STJ produced the right value" rather than "STJ deserialise happens to invert STJ
    /// serialise."
    /// </summary>
    private static void AssertStructurallyEqual(object expected, object actual)
    {
        var expectedJson = JToken.Parse(System.Text.Encoding.UTF8.GetString(NewtonsoftReferenceSerializer.Serialize((Message)expected)));
        var actualJson = JToken.Parse(System.Text.Encoding.UTF8.GetString(NewtonsoftReferenceSerializer.Serialize((Message)actual)));
        Assert.True(JToken.DeepEquals(expectedJson, actualJson),
            $"Expected:\n{expectedJson}\nActual:\n{actualJson}");
    }

    private static bool JsonElementsEqual(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var aProps = a.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                var bProps = b.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                if (aProps.Count != bProps.Count) return false;
                for (var i = 0; i < aProps.Count; i++)
                {
                    if (aProps[i].Name != bProps[i].Name) return false;
                    if (!JsonElementsEqual(aProps[i].Value, bProps[i].Value)) return false;
                }
                return true;

            case JsonValueKind.Array:
                var aItems = a.EnumerateArray().ToList();
                var bItems = b.EnumerateArray().ToList();
                if (aItems.Count != bItems.Count) return false;
                for (var i = 0; i < aItems.Count; i++)
                {
                    if (!JsonElementsEqual(aItems[i], bItems[i])) return false;
                }
                return true;

            case JsonValueKind.String:
                return a.GetString() == b.GetString();

            case JsonValueKind.Number:
                // Compare via string form; both impls emit canonical .NET numeric formatting and
                // any divergence (e.g. trailing zeros on decimals) is itself a wire-compat finding.
                return a.GetRawText() == b.GetRawText();

            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return true;

            default:
                return a.GetRawText() == b.GetRawText();
        }
    }
}
```

- [ ] **Step 4: Create the pathological-input tests**

Create `src/ServiceConnect.SerializationCompatTests/PathologicalInputTests.cs`:

```csharp
using System.Text;
using System.Text.Json;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.SerializationCompatTests;

/// <summary>
/// STJ-side guard rails for inputs that Newtonsoft tolerated but STJ rejects (or vice versa).
/// These tests document the v8 wire-format edge cases that the release notes call out.
/// </summary>
public class PathologicalInputTests
{
    private static readonly SystemTextJsonMessageSerializer Stj = new();

    [Fact]
    public void DeeplyNested_BeyondMaxDepth_Throws()
    {
        // 50-level nested array exceeds MaxDepth = 32; STJ should throw on parse.
        var nested = new StringBuilder();
        for (var i = 0; i < 50; i++) nested.Append('[');
        nested.Append("0");
        for (var i = 0; i < 50; i++) nested.Append(']');
        // Wrap as a message for the deserializer's signature.
        var json = $"{{\"CorrelationId\":\"00000000-0000-0000-0000-000000000000\",\"Field\":{nested}}}";
        var bytes = Encoding.UTF8.GetBytes(json);

        Assert.ThrowsAny<Interfaces.Exceptions.SerializationException>(() =>
            Stj.Deserialize(bytes, typeof(NestedArrayMessage)));
    }

    [Fact]
    public void NaN_Double_Rejected()
    {
        // Explicitly malformed JSON: "NaN" is not a valid JSON literal. Newtonsoft tolerated
        // it; STJ rejects. Documented behaviour change in v8 release notes.
        var json = "{\"CorrelationId\":\"00000000-0000-0000-0000-000000000000\",\"Value\":NaN}";
        var bytes = Encoding.UTF8.GetBytes(json);

        Assert.ThrowsAny<Interfaces.Exceptions.SerializationException>(() =>
            Stj.Deserialize(bytes, typeof(DoubleMessage)));
    }

    [Fact]
    public void StringEncodedNumber_AcceptedByDefault()
    {
        // Newtonsoft accepts "3" for an int field. STJ does too, with NumberHandling.AllowReadingFromString.
        var json = "{\"CorrelationId\":\"00000000-0000-0000-0000-000000000000\",\"Value\":\"42\"}";
        var bytes = Encoding.UTF8.GetBytes(json);

        var result = (Int32Message)Stj.Deserialize(bytes, typeof(Int32Message));
        Assert.Equal(42, result.Value);
    }

    private sealed class NestedArrayMessage : Message
    {
        public NestedArrayMessage() : base(Guid.Empty) { }
        public object Field { get; init; } = default!;
    }

    private sealed class DoubleMessage : Message
    {
        public DoubleMessage() : base(Guid.Empty) { }
        public double Value { get; init; }
    }

    private sealed class Int32Message : Message
    {
        public Int32Message() : base(Guid.Empty) { }
        public int Value { get; init; }
    }
}
```

- [ ] **Step 5: Build the test project and run all corpus tests**

Run: `dotnet build src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj`
Expected: 0 errors, 0 warnings.

Run: `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj --no-build --no-restore`
Expected: every corpus item passes all four round-trip assertions, plus the three pathological-input tests pass. Total: ~36 corpus tests + 3 pathological = ~39 tests.

If a test fails, read the failure and decide whether the corpus or the STJ settings are wrong:
- If STJ output differs from Newtonsoft because of a settings mismatch (e.g., date format), update `SystemTextJsonMessageSerializer`'s `JsonSerializerOptions` to match. Add a test if a new edge case surfaced.
- If the failure is genuine (a Newtonsoft tolerance STJ doesn't replicate), document in the release notes and accept the break — but only after confirming nothing in the production message types relies on the tolerance.

- [ ] **Step 6: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add \
  src/ServiceConnect.SerializationCompatTests/Corpus/CorpusTypes.cs \
  src/ServiceConnect.SerializationCompatTests/Corpus/CorpusFactory.cs \
  src/ServiceConnect.SerializationCompatTests/RoundTripTests.cs \
  src/ServiceConnect.SerializationCompatTests/PathologicalInputTests.cs

git commit -m "$(cat <<'EOF'
test(serialization-compat): wire-compat corpus + round-trip assertions

Representative corpus of Message-derived types (primitives, collections,
nullables, nested objects, dates, enums, byte[], polymorphism via base
class, empty) drives four round-trip assertions per item:

  1. STJ -> STJ structural equality (control).
  2. Newtonsoft -> STJ deserialise (v7 producer -> v8 consumer).
  3. STJ -> Newtonsoft deserialise (v8 producer -> v7 consumer).
  4. STJ output and Newtonsoft output parse to the same JSON DOM.

Plus pathological-input tests document v8 behaviour changes:
- Deeply-nested input (>MaxDepth=32) is rejected.
- NaN/Infinity doubles are rejected by STJ.
- String-encoded numbers ("42" -> int) are accepted (matches Newtonsoft).

Production DI is unchanged — Newtonsoft is still the active serializer.
The corpus exists to enforce wire-compat *before* Task 6 swaps the
production serializer to STJ.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Atomic migration — interface, body cascade, IProducer surface, DI swap

The big commit. Reduces `IMessageSerializer` from 9 methods to 3, deletes the Newtonsoft impl from production, switches DI to STJ, retypes `SendContext.MessageBytes` and `IProducer`'s body parameters to `ReadOnlyMemory<byte>`, and tightens `IProducer`/`OutboundHeaderBuilder` headers to `IReadOnlyDictionary`. Build is broken in intermediate states; commit once at the end.

**Files modified (production):**
- `src/ServiceConnect.Interfaces/Messages/IMessageSerializer.cs`
- `src/ServiceConnect/Services/SystemTextJsonMessageSerializer.cs`
- `src/ServiceConnect.Interfaces/Pipelines/SendContext.cs`
- `src/ServiceConnect.Interfaces/Bus/IProducer.cs`
- `src/ServiceConnect/Bus.cs`
- `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs`
- `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs`
- `src/ServiceConnect/ServiceConnectBuilder.cs` (or wherever `NewtonsoftJsonMessageSerializer` is registered — verify in Step 1)

**Files deleted:**
- `src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs`

**Test files modified:**
- Any test that constructs `NewtonsoftJsonMessageSerializer` directly. Find with `grep -rn 'NewtonsoftJsonMessageSerializer\|new NewtonsoftJsonMessageSerializer' src/ --include='*.cs' | grep -v 'obj/\|bin/'` in Step 1.
- Any test that asserts on `IMessageSerializer` methods removed in this commit (the `byte[]` overloads, `ReadOnlySpan<byte>` overloads, `ReadOnlySequence<byte>` overload). Find these by building and reading errors.

- [ ] **Step 1: Inventory the call sites that will break**

Run from `/home/tim/source/ServiceConnect-CSharp`:

```bash
grep -rn 'NewtonsoftJsonMessageSerializer' src/ --include='*.cs' | grep -v 'obj/\|bin/'
grep -rn '_serializer\.Serialize\|_serializer\.Deserialize' src/ --include='*.cs' | grep -v 'obj/\|bin/'
grep -rn 'IMessageSerializer' src/ --include='*.cs' | grep -v 'obj/\|bin/' | head -30
```

Note every hit. The migration must update every consumer atomically — the build will be broken until each is updated.

- [ ] **Step 2: Reduce `IMessageSerializer` interface to 3 methods**

Replace `src/ServiceConnect.Interfaces/Messages/IMessageSerializer.cs` entirely:

```csharp
using System.Buffers;

namespace ServiceConnect.Interfaces;

/// <summary>
/// Serializes and deserializes <see cref="Message"/> instances.
/// </summary>
public interface IMessageSerializer
{
    /// <summary>
    /// Serializes <paramref name="message"/> directly into <paramref name="output"/>.
    /// Implementations should write without allocating an intermediate <see cref="byte"/> array.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="message">The message to serialize.</param>
    /// <param name="output">The destination buffer writer. Caller owns its lifetime.</param>
    void Serialize<T>(T message, IBufferWriter<byte> output) where T : Message;

    /// <summary>
    /// Deserializes a message of the specified expected type from <paramref name="data"/>.
    /// </summary>
    T Deserialize<T>(ReadOnlyMemory<byte> data) where T : Message;

    /// <summary>
    /// Deserializes a message of the runtime-supplied <paramref name="type"/>.
    /// Used by the dispatch path which resolves the CLR type from the registry.
    /// </summary>
    object Deserialize(ReadOnlyMemory<byte> data, Type type);
}
```

- [ ] **Step 3: Reduce `SystemTextJsonMessageSerializer` to match the 3-method interface**

Edit `src/ServiceConnect/Services/SystemTextJsonMessageSerializer.cs`. Delete the now-removed methods (`Serialize<T>(T)` byte[] return, `Deserialize<T>(byte[])`, `Deserialize<T>(ReadOnlySpan<byte>)`, `Deserialize(byte[], Type)`, `Deserialize(ReadOnlySpan<byte>, Type)`, `Deserialize(in ReadOnlySequence<byte>, Type)`). The class becomes:

```csharp
using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Services;

/// <summary>
/// System.Text.Json implementation of <see cref="IMessageSerializer"/>. Wire format
/// is JSON-equivalent to the v7 Newtonsoft implementation under matching settings;
/// see SerializationCompatTests for the cross-version round-trip corpus.
/// </summary>
public sealed class SystemTextJsonMessageSerializer : IMessageSerializer
{
    private readonly JsonSerializerOptions _options;

    public SystemTextJsonMessageSerializer(JsonSerializerOptions? options = null)
    {
        _options = new JsonSerializerOptions(options ?? new JsonSerializerOptions())
        {
            MaxDepth = 32,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = false,
            IncludeFields = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
    }

    public void Serialize<T>(T message, IBufferWriter<byte> output) where T : Message
    {
        ArgumentNullException.ThrowIfNull(output);
        if (message is null)
        {
            throw new SerializationException("Cannot serialize null message", typeof(T));
        }

        try
        {
            using var jsonWriter = new Utf8JsonWriter(output);
            JsonSerializer.Serialize(jsonWriter, message, message.GetType(), _options);
        }
        catch (JsonException ex)
        {
            throw new SerializationException($"Failed to serialize message of type {typeof(T).Name}", typeof(T), ex);
        }
    }

    public T Deserialize<T>(ReadOnlyMemory<byte> data) where T : Message
        => (T)Deserialize(data, typeof(T));

    public object Deserialize(ReadOnlyMemory<byte> data, Type type)
    {
        try
        {
            return JsonSerializer.Deserialize(data.Span, type, _options)
                ?? throw new SerializationException(
                    $"Deserialization returned null for type {type.Name}", type);
        }
        catch (JsonException ex)
        {
            throw new SerializationException(
                $"Failed to deserialize message of type {type.Name}", type, ex);
        }
    }
}
```

- [ ] **Step 4: Retype `SendContext.MessageBytes` to `ReadOnlyMemory<byte>`**

Edit `src/ServiceConnect.Interfaces/Pipelines/SendContext.cs:17`:

```csharp
// Before:
public required byte[] MessageBytes { get; init; }

// After:
public required ReadOnlyMemory<byte> MessageBytes { get; init; }
```

XML doc above (line 16) is unchanged — "the serialized message body, exactly as the producer will send it" is still correct.

- [ ] **Step 5: Retype `IProducer` body parameters and tighten headers**

Edit `src/ServiceConnect.Interfaces/Bus/IProducer.cs`. Replace lines 11–28:

```csharp
// Before (4 method signatures with byte[] body and IDictionary<string,string>? headers):
Task PublishAsync(Type type, byte[] message, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
Task SendAsync(Type type, byte[] message, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
Task SendAsync(string endPoint, Type type, byte[] message, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
Task SendBytesAsync(string endPoint, Type type, byte[] packet, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);

// After:
Task PublishAsync(Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
Task SendAsync(Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
Task SendAsync(string endPoint, Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
Task SendBytesAsync(string endPoint, Type type, ReadOnlyMemory<byte> packet, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
```

The parameter name `message` is renamed to `body` to align with the new shape's framing (it's no longer a typed message — it's the serialized body bytes).

- [ ] **Step 6: Update `Bus.cs` to use ArrayBufferWriter and propagate ROM<byte>**

Edit `src/ServiceConnect/Bus.cs`. Two patterns to update.

**Pattern A: every `_serializer.Serialize(message)` call site changes shape.**

Find each call (there are two — `PublishAsync` around line 95 and `SendAsync` around line 149 — both call sites use the same shape):

```csharp
// Before:
var messageBytes = _serializer.Serialize(message);

// After:
var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
_serializer.Serialize(message, bufferWriter);
var messageBytes = bufferWriter.WrittenMemory;
```

**Pattern B: `SendContext.MessageBytes = messageBytes` continues to compile** because both sides are now `ReadOnlyMemory<byte>`. No change needed at lines 122, 175, 190.

- [ ] **Step 7: Update `Producer.cs` body parameters and drop AsMemory casts**

Edit `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs`. Four method signatures change at lines 152, 203, 263, 310:

```csharp
// Before each:
public async Task PublishAsync(Type type, byte[] message, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
public async Task SendAsync(Type type, byte[] message, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
public async Task SendAsync(string endPoint, Type type, byte[] message, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
public async Task SendBytesAsync(string endPoint, Type type, byte[] packet, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)

// After each:
public async Task PublishAsync(Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
public async Task SendAsync(Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
public async Task SendAsync(string endPoint, Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
public async Task SendBytesAsync(string endPoint, Type type, ReadOnlyMemory<byte> packet, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
```

**Find and remove every `(ReadOnlyMemory<byte>)` cast in this file** — the casts at the four publish sites (around lines 189, 247, 295, 342 per the spec) become identity now that the parameter is already `ROM<byte>`. Replace `(ReadOnlyMemory<byte>)message` with `body` (or `packet` for the last method) directly.

If `message` is referenced inside the method bodies elsewhere (e.g., in log messages), rename to `body` for consistency.

- [ ] **Step 8: Update `OutboundHeaderBuilder.BuildHeaders` headers parameter**

Edit `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs:49`:

```csharp
// Before:
public Dictionary<string, object> BuildHeaders(Type type, IDictionary<string, string>? headers, string queueName, string messageType)

// After:
public Dictionary<string, object> BuildHeaders(Type type, IReadOnlyDictionary<string, string>? headers, string queueName, string messageType)
```

The body uses `headers.Count` and `foreach (var kvp in headers)` — both are valid on `IReadOnlyDictionary<string,string>`. No body change.

- [ ] **Step 9: Switch DI registration from Newtonsoft to STJ**

Find the registration of `NewtonsoftJsonMessageSerializer`:

```bash
grep -rn 'NewtonsoftJsonMessageSerializer' src/ --include='*.cs' | grep -v 'obj/\|bin/'
```

Likely in `src/ServiceConnect/ServiceConnectBuilder.cs` or `src/ServiceConnect/ServiceCollectionExtensions.cs`. Wherever it is, change:

```csharp
// Before (example shape):
services.TryAddSingleton<IMessageSerializer, NewtonsoftJsonMessageSerializer>();

// After:
services.TryAddSingleton<IMessageSerializer, SystemTextJsonMessageSerializer>();
```

If the registration uses a factory delegate that constructs `NewtonsoftJsonMessageSerializer` with optional `JsonSerializerSettings`, switch to constructing `SystemTextJsonMessageSerializer` with optional `JsonSerializerOptions` — or remove the factory and use the type-resolution form above if the default-options ctor is sufficient.

- [ ] **Step 10: Delete `NewtonsoftJsonMessageSerializer.cs`**

```bash
git rm src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs
```

- [ ] **Step 11: Update unit tests that constructed `NewtonsoftJsonMessageSerializer` directly**

Re-run the inventory grep:
```bash
grep -rn 'NewtonsoftJsonMessageSerializer' src/ --include='*.cs' | grep -v 'obj/\|bin/'
```

Every remaining hit (now in `src/ServiceConnect.UnitTests/` only — the SerializationCompatTests project uses the fixture, not the production class) needs to change. The fix for each hit:
- `new NewtonsoftJsonMessageSerializer()` → `new SystemTextJsonMessageSerializer()`
- `new NewtonsoftJsonMessageSerializer(settings)` (Newtonsoft `JsonSerializerSettings`) → `new SystemTextJsonMessageSerializer(options)` where `options` is a `JsonSerializerOptions`. If a test passed Newtonsoft-specific settings, port them to STJ equivalents. If the test was simply opting in to the default settings, drop the constructor argument.

If a test calls `_serializer.Serialize(message)` (the dropped `byte[]`-returning overload) or `_serializer.Deserialize(byte[])`, update to:
- `_serializer.Serialize(message)` → `var bw = new ArrayBufferWriter<byte>(); _serializer.Serialize(message, bw); var bytes = bw.WrittenMemory.ToArray();`
- `_serializer.Deserialize<T>(byte[] data)` → `_serializer.Deserialize<T>((ReadOnlyMemory<byte>)data.AsMemory())`

- [ ] **Step 12: Variance audit (per A.1 lesson #2)**

Run from `/home/tim/source/ServiceConnect-CSharp`:

```bash
grep -nE 'as IList<|as ICollection<|as IDictionary<|as IReadOnlyList<|as IReadOnlyCollection<|as Span<|as Memory<|as ReadOnlySpan<|as ReadOnlyMemory<' \
  src/ServiceConnect/Bus.cs \
  src/ServiceConnect/Services/*.cs \
  src/ServiceConnect.Client.RabbitMQ/Producer/*.cs
```

For every hit, verify the cast still has a runtime-realistic chance of succeeding. Record any dead branches and remove them inline (this is the equivalent of f9624572 in Phase A.1). If you find a dead `as ROM<byte>` branch — for example, a fallback cast that's now identity — replace with the direct expression.

- [ ] **Step 13: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

If the build fails with errors in files you didn't touch, those are downstream consumers of the changed interfaces. Fix them mechanically:
- Test files passing `byte[]` to `IProducer` methods → wrap with `.AsMemory()` or accept the implicit conversion (`byte[]` to `ReadOnlyMemory<byte>` is implicit).
- Test files asserting on `_serializer.Serialize` returning `byte[]` → update to use `ArrayBufferWriter` pattern.
- Mocks using `It.IsAny<byte[]>()` for IProducer body → change to `It.IsAny<ReadOnlyMemory<byte>>()`.

Iterate until the full solution build is clean.

- [ ] **Step 14: Run the full unit-test suite**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore`
Expected: all tests pass.

If a test fails with a behavioural change (not just an interface-shape issue), investigate. Common surprises:
- A test that asserted Newtonsoft-style date formatting (e.g., `"2026-05-03T12:00:00Z"` vs `"2026-05-03T12:00:00+00:00"`) may need the assertion updated to match STJ output. The corpus tests prove the cross-version round-trip works; per-test exact-string assertions on serialized JSON are the candidates for adjustment.
- A test that injected a `JsonSerializerSettings` to opt in to a Newtonsoft-specific behaviour needs the equivalent `JsonSerializerOptions` configuration.

- [ ] **Step 15: Run the SerializationCompatTests suite**

Run: `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj --no-build --no-restore`
Expected: all corpus and pathological-input tests pass. (They passed in Task 5; this re-run confirms no regression in the production STJ impl after the interface reduction.)

- [ ] **Step 16: Run the EndToEndTests build (per A.1 lesson #1, full-solution gate)**

Run: `dotnet build src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`
Expected: 0 errors, 0 warnings. (E2E tests reference IProducer and IMessageSerializer indirectly; this catches the EndToEndTests-collateral case from Phase A.1.)

If E2E tests fail to compile, the most likely cause is a test passing `byte[]` to an `IProducer` method or an `IMessageSerializer` method that was reduced. Apply the same mechanical fixes as Step 13.

- [ ] **Step 17: Commit Tasks 6's accumulated changes atomically**

```bash
git add \
  src/ServiceConnect.Interfaces/Messages/IMessageSerializer.cs \
  src/ServiceConnect.Interfaces/Pipelines/SendContext.cs \
  src/ServiceConnect.Interfaces/Bus/IProducer.cs \
  src/ServiceConnect/Services/SystemTextJsonMessageSerializer.cs \
  src/ServiceConnect/Bus.cs \
  src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs \
  src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs

# Plus:
# - the DI-registration file from Step 9 (find with: git status --short)
# - any test files modified in Step 11
# - any downstream consumer files modified in Step 13
# - the deletion of NewtonsoftJsonMessageSerializer.cs (already staged via git rm in Step 10)

# Verify nothing extra slipped in:
git status --short

git commit -m "$(cat <<'EOF'
refactor(serializer)!: STJ replaces Newtonsoft, IMessageSerializer is 3 methods, body cascade lands

IMessageSerializer reduced from 9 methods to 3:
  void Serialize<T>(T, IBufferWriter<byte>)
  T Deserialize<T>(ReadOnlyMemory<byte>)
  object Deserialize(ReadOnlyMemory<byte>, Type)

The byte[] paths and ReadOnlySpan<byte>/ReadOnlySequence<byte> overloads
are dropped. Callers needing byte[] use ArrayBufferWriter and read
WrittenMemory.ToArray(); callers with byte[] pass AsMemory().

Production serializer is now SystemTextJsonMessageSerializer, with
JsonSerializerOptions configured to keep wire-compat with v7 Newtonsoft:
MaxDepth = 32, UnsafeRelaxedJsonEscaping, NumberHandling.AllowReadingFromString,
DefaultIgnoreCondition.Never.

NewtonsoftJsonMessageSerializer is deleted from the production codebase.
SerializationCompatTests now backstops the wire-format guarantee with
~36 corpus round-trip assertions across 9 representative Message types
plus pathological-input tests for the documented v8 behaviour changes
(NaN/Infinity rejection, MaxDepth = 32).

Body-type cascade lands together:
- SendContext.MessageBytes: byte[] -> ReadOnlyMemory<byte> (init-only;
  middleware mutates Headers, never bytes — see spec Section 3 rationale).
- IProducer.{Publish,Send,SendBytes}Async: body is ReadOnlyMemory<byte>;
  headers is IReadOnlyDictionary<string,string>?.
- Bus.cs: Serialize call sites use ArrayBufferWriter<byte> and propagate
  WrittenMemory through SendContext.MessageBytes downstream.
- Producer.cs: drops the four (ReadOnlyMemory<byte>)cast sites — body
  is already ROM<byte> end-to-end.
- OutboundHeaderBuilder.BuildHeaders headers param tightened to
  IReadOnlyDictionary<string,string>?.

BREAKING CHANGE: every consumer of IMessageSerializer or IProducer must
update. Specifically: callers passing byte[] to IProducer.{Publish,Send,
SendBytes}Async must wrap with .AsMemory() or accept the implicit
conversion. Callers using IMessageSerializer.Serialize<T>(T) returning
byte[] must switch to the IBufferWriter<byte> overload. Callers using
the dropped Deserialize byte[]/ReadOnlySpan/ReadOnlySequence overloads
must use the ReadOnlyMemory<byte> form.

Wire compatibility for v7<->v8 deployments is guaranteed by the corpus
tests in src/ServiceConnect.SerializationCompatTests; CI runs them on
every PR.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Remove Newtonsoft.Json from shipped packages

**Files:**
- Modify: `src/ServiceConnect/ServiceConnect.csproj`
- Delete: `src/ServiceConnect/Services/IO/ReadOnlyMemoryStream.cs` (verify no consumer remains first)
- Delete: `src/ServiceConnect/Services/IO/ReadOnlySequenceStream.cs` (verify no consumer remains first)

- [ ] **Step 1: Remove the Newtonsoft package reference**

Edit `src/ServiceConnect/ServiceConnect.csproj`. Find:

```xml
<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
```

Remove that line. Save.

- [ ] **Step 2: Verify no production code references Newtonsoft anymore**

Run: `grep -rn 'using Newtonsoft\|Newtonsoft\.Json' src/ServiceConnect/ src/ServiceConnect.Interfaces/ src/ServiceConnect.Client.RabbitMQ/ src/ServiceConnect.Persistence.* src/ServiceConnect.HealthChecks/ src/ServiceConnect.Telemetry/ --include='*.cs' | grep -v 'obj/\|bin/'`
Expected: no hits in the listed production projects.

If hits remain, they're consumers we missed in Task 6. Update each:
- `using Newtonsoft.Json;` directives in production files: switch to `using System.Text.Json;` or remove if unused.
- `JsonConvert.SerializeObject(...)` calls: replace with `JsonSerializer.Serialize(...)` (STJ).

The `DeepClone.cs` helper in `src/ServiceConnect.Persistence.InMemory/` uses `Newtonsoft.Json` per the comment we read in Phase A.1 — that's a SEPARATE concern (in-memory persistence DeepClone, not the message serializer) and IS NOT in scope for Phase A.2. Leave it. The InMemory persistence project keeps its own Newtonsoft package reference if it has one.

- [ ] **Step 3: Verify `ReadOnlyMemoryStream.cs` and `ReadOnlySequenceStream.cs` are unused**

These IO wrappers existed to let `NewtonsoftJsonMessageSerializer.Deserialize(ROM<byte>, Type)` and `Deserialize(ROS<byte>, Type)` wrap the buffers as `Stream` for `JsonTextReader`. With Newtonsoft removed, they may be orphaned.

Run: `grep -rn 'ReadOnlyMemoryStream\|ReadOnlySequenceStream' src/ --include='*.cs' | grep -v 'obj/\|bin/'`

If the only hits are the file definitions themselves, they are unused and safe to delete:

```bash
git rm src/ServiceConnect/Services/IO/ReadOnlyMemoryStream.cs
git rm src/ServiceConnect/Services/IO/ReadOnlySequenceStream.cs
```

If hits remain elsewhere (e.g., a test or production consumer that depended on them), DO NOT delete — investigate and update separately.

- [ ] **Step 4: Confirm test projects keep their Newtonsoft references**

Verify the SerializationCompatTests project still references Newtonsoft (it intentionally does — it's the v7 reference impl):

```bash
grep 'Newtonsoft' src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj
```
Expected: one `<PackageReference Include="Newtonsoft.Json" ... />` line.

Verify the InMemoryPersistence project — if it uses DeepClone (which uses Newtonsoft) and isn't being changed in Phase A.2, leave its Newtonsoft reference alone.

```bash
grep 'Newtonsoft' src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj
```

If a Newtonsoft reference is here, it stays for now (out of scope for A.2).

- [ ] **Step 5: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings. The shipped `ServiceConnect` package no longer pulls Newtonsoft.

- [ ] **Step 6: Full unit-test sweep**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore`
Expected: all tests pass.

- [ ] **Step 7: SerializationCompatTests final pass**

Run: `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj --no-build --no-restore`
Expected: all wire-compat tests pass — Newtonsoft is referenced only here, and STJ is now production.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect/ServiceConnect.csproj
# Plus the two deletions if Step 3 confirmed they were orphaned:
# (already staged via git rm in Step 3)

git commit -m "$(cat <<'EOF'
chore(packages)!: remove Newtonsoft.Json from shipped ServiceConnect package

After the STJ migration in the previous commit, the production
ServiceConnect assembly has no remaining Newtonsoft.Json reference.
Remove the package dependency so consumers do not transitively pull
Newtonsoft when they install the v8 ServiceConnect package.

ReadOnlyMemoryStream and ReadOnlySequenceStream — IO wrappers used only
by the deleted Newtonsoft serializer — are also removed.

Newtonsoft remains a dependency of:
- src/ServiceConnect.SerializationCompatTests (test-only — the v7
  reference fixture for cross-version wire-compat).
- src/ServiceConnect.Persistence.InMemory (DeepClone helper — separate
  concern from the message serializer, out of scope for Phase A.2).

BREAKING CHANGE: consumers who installed ServiceConnect for transitive
access to Newtonsoft.Json must add their own direct reference. The
recommended path forward is to use System.Text.Json directly or migrate
their own JSON code.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Final phase verification + roadmap update

**Files:**
- Modify: `architecture-fix-plan.md` (status update commit at the end). All other steps are read-only verification.

- [ ] **Step 1: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Full unit-test sweep**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore`
Expected: all tests pass.

- [ ] **Step 3: SerializationCompatTests sweep**

Run: `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj --no-build --no-restore`
Expected: all corpus and pathological-input tests pass (~39 tests).

- [ ] **Step 4: Verify Newtonsoft scope**

Run: `find src -name '*.csproj' -exec grep -l 'Newtonsoft.Json' {} \;`
Expected: only:
- `src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj`
- `src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj` (DeepClone — out of scope, may or may not be present depending on prior history)

If `src/ServiceConnect/ServiceConnect.csproj` appears in the list, Step 1 of Task 7 was incomplete. Fix and recommit.

- [ ] **Step 5: Inspect the commit history**

Run: `git log --oneline 9829be82..HEAD` (`9829be82` is the last Phase A.1 commit).
Expected — Phase A.2 produced (in chronological order):
- A spec-correction commit (the pre-flight `e79217dc` from before Task 1).
- Tasks 2-7 commits (project skeleton, fixture, STJ alongside, corpus, atomic migration, package removal).
- Plus any small follow-up commits a reviewer caught.

- [ ] **Step 6: Sanity-check the diff against the plan**

Run: `git diff --stat e79217dc..HEAD` (where `e79217dc` is the spec-correction commit just before Phase A.2 implementation began).

The expected file set is:
- New project: `src/ServiceConnect.SerializationCompatTests/` (csproj + ~6 .cs files).
- Modified: `src/ServiceConnect/Services/SystemTextJsonMessageSerializer.cs`, `src/ServiceConnect.Interfaces/Messages/IMessageSerializer.cs`, `src/ServiceConnect.Interfaces/Pipelines/SendContext.cs`, `src/ServiceConnect.Interfaces/Bus/IProducer.cs`, `src/ServiceConnect/Bus.cs`, `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs`, `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs`, the DI-registration file, `src/ServiceConnect/ServiceConnect.csproj`, `src/ServiceConnect.slnx`, plus modified test files.
- Deleted: `src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs`, optionally the two IO Stream wrappers.

No surprises — anything outside that pattern is suspicious.

- [ ] **Step 7: Update phase status in the roadmap**

Edit `architecture-fix-plan.md`. Find:
```
## Group A — v8 public-API tightening · *Phase A.1 done; A.2 + A.3 pending*
```

Change to:
```
## Group A — v8 public-API tightening · *Phase A.2 done; A.3 pending*
```

Commit:

```bash
git add architecture-fix-plan.md
git commit -m "$(cat <<'EOF'
docs(architecture): mark Phase A.2 done in the fix plan

Phase A.2 of Group A (v8 public-API tightening) is complete:
- IMessageSerializer reduced from 9 methods to 3 (zero-copy IBufferWriter
  on the send side; ReadOnlyMemory<byte> on the receive side).
- System.Text.Json replaces Newtonsoft.Json in the shipped ServiceConnect
  package; Newtonsoft remains in the SerializationCompatTests test
  project as the v7 reference fixture.
- Wire-compat between v7 and v8 producers/consumers is enforced by a
  cross-version round-trip corpus on every PR.
- SendContext.MessageBytes and IProducer.{Publish,Send,SendBytes}Async
  body parameters retyped to ReadOnlyMemory<byte>; IProducer headers
  parameter tightened to IReadOnlyDictionary<string,string>?.

Phase A.3 is queued (IBus.SendToManyAsync, RouteAsync, IConsumer,
IMessageDispatcher, IConsumeContext + ReplyOptions, examples).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Risks and rollback

- **Wire-compat regression**: a corpus item passes locally but a v7 producer's actual production payload deserialises differently under STJ. Mitigation: the corpus is intentionally broad (primitives, collections, nested, dates, enums, byte[], polymorphism, empty); a regression of a corpus assertion is the early warning. If a real-world payload trips a case the corpus doesn't cover, add the case to the corpus and re-verify.
- **NaN/Infinity rejection**: documented behaviour change in the Task 6 commit message. Rolls forward via release notes; consumers with such payloads add a custom converter.
- **Custom Newtonsoft converters in user code**: any consumer of `NewtonsoftJsonMessageSerializer(JsonSerializerSettings)` who passed custom Newtonsoft converters must port them to STJ `JsonConverter<T>` implementations. Documented in the Task 6 commit message and v8 release notes.
- **Rollback** is `git revert` of Tasks 6 and 7 (in that order). Tasks 2–5 are additive (new project + new file alongside Newtonsoft) and reverting them is optional cleanup, not a correctness requirement.

---

## Decisions banked from earlier brainstorm

For audit trail (these came out of Group A's brainstorm):

- **Q1**: deprecation posture — clean break, no `[Obsolete]` shims.
- **Q2**: serializer redesign timing — STJ in v8 + cross-version test corpus.
- **Q5a**: `IMessageSerializer.Serialize` shape — drop `byte[] Serialize<T>`; primary path is `IBufferWriter<byte>`.
- **Q5b**: `IProducer` body type — `ReadOnlyMemory<byte>` everywhere; threads zero-copy buffers end-to-end.
- **Q6c**: `IReadOnly*` on transport contracts — `IProducer.{Publish,Send,SendBytes}Async` headers tighten in this phase.
- **Spec post-correction (`e79217dc`)**: `SendContext.MessageBytes` is init-only (was specified as `set`); `Envelope.Body` was already done — no Phase A.2 change needed.
