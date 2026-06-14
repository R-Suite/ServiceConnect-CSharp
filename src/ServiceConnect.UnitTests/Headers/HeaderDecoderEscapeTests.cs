using System.Text.Json.Nodes;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Headers;

public class HeaderDecoderEscapeTests
{
    [Theory]
    [InlineData("\\")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\t")]
    [InlineData("\"")]
    [InlineData("\b")]
    [InlineData("\f")]
    [InlineData("\x01")]
    public void Decode_StringWithSpecialChars_RoundTripsThroughJson(string input)
    {
        var dict = new Dictionary<string, object> { ["k"] = input };
        var rendered = HeaderDecoder.Decode(dict);

        Assert.NotNull(rendered);
        // Rendered must be valid JSON.
        var parsed = JsonNode.Parse(rendered!);
        Assert.NotNull(parsed);
        Assert.Equal(input, parsed!["k"]!.GetValue<string>());
    }
}
