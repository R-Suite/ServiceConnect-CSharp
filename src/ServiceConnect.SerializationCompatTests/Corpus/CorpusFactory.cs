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
        yield return [NullableAllNull()];
        yield return [NullablePopulated()];
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

    public static NullableMessage NullableAllNull() => new(TestCorrelationId)
    {
        NullableInt = null,
        NullableString = null,
        NullableDateTime = null,
    };

    public static NullableMessage NullablePopulated() => new(TestCorrelationId)
    {
        NullableInt = 7,
        NullableString = "present",
        NullableDateTime = new DateTime(2026, 5, 3, 12, 0, 0, DateTimeKind.Utc),
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
