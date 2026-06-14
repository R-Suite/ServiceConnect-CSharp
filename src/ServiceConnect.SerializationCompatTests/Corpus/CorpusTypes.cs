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

// ---- Concrete derived type as its own static type (no $type metadata) ----
//
// Pet is declared as Dog, the concrete derived type. Both serialisers therefore
// see Dog's full property set on serialise and reconstruct Dog on deserialise.
// This is NOT a test of "abstract base + runtime-polymorphic derived" — that
// case (declared type Animal, runtime type Dog) is intentionally omitted because
// neither STJ default nor Newtonsoft with TypeNameHandling.None would carry
// Dog's `Breed` property across the wire (no $type discriminator), and STJ
// further refuses to instantiate the abstract Animal on deserialise. If
// abstract-base polymorphism ever becomes a supported scenario, add a separate
// corpus item that asserts the chosen $type-discrimination strategy.
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
