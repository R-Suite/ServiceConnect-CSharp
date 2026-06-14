using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Headers;

public class HeaderDecoderDepthTests
{
    [Fact]
    public void Decode_NestedDictionaryExceedingDepthLimit_ReturnsTypeNameFallback()
    {
        // Build a 33-deep dictionary chain. Decode wraps Render in try/catch and
        // falls back to typeof().FullName on any exception, so the depth-limit
        // throw produces the type-name fallback rather than propagating.
        IDictionary<string, object> root = new Dictionary<string, object>();
        IDictionary<string, object> current = root;
        for (var i = 0; i < 33; i++)
        {
            var inner = new Dictionary<string, object>();
            current["nested"] = inner;
            current = inner;
        }

        var rendered = HeaderDecoder.Decode(root);
        // Decode's catch falls back to type FullName for the bad input.
        Assert.Equal(root.GetType().FullName, rendered);
    }

    [Fact]
    public void Decode_NestedDictionaryAtDepthLimit_RendersSuccessfully()
    {
        // Boundary check: a chain with 31 nested dictionaries plus a leaf reaches
        // depth 32 inside Render without exceeding it. Should render without throwing.
        IDictionary<string, object> root = new Dictionary<string, object>();
        IDictionary<string, object> current = root;
        for (var i = 0; i < 31; i++)
        {
            var inner = new Dictionary<string, object>();
            current["nested"] = inner;
            current = inner;
        }
        current["leaf"] = "value";

        var rendered = HeaderDecoder.Decode(root);
        Assert.NotNull(rendered);
        Assert.StartsWith("{", rendered);
    }
}
