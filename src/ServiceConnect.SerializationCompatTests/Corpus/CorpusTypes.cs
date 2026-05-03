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
