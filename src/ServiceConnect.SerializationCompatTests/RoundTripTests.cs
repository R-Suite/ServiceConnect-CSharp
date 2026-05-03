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
                if (aProps.Count != bProps.Count)
                {
                    return false;
                }

                for (var i = 0; i < aProps.Count; i++)
                {
                    if (aProps[i].Name != bProps[i].Name)
                    {
                        return false;
                    }

                    if (!JsonElementsEqual(aProps[i].Value, bProps[i].Value))
                    {
                        return false;
                    }
                }
                return true;

            case JsonValueKind.Array:
                var aItems = a.EnumerateArray().ToList();
                var bItems = b.EnumerateArray().ToList();
                if (aItems.Count != bItems.Count)
                {
                    return false;
                }

                for (var i = 0; i < aItems.Count; i++)
                {
                    if (!JsonElementsEqual(aItems[i], bItems[i]))
                    {
                        return false;
                    }
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
