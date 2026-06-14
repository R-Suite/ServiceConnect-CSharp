using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

public sealed class ServiceConnectActivitySourceTruncateTests
{
    [Fact]
    public void Truncate_AtSurrogatePairBoundary_DoesNotOrphanHighSurrogate()
    {
        // U+1F600 ("grinning face" emoji) is a non-BMP code point encoded as the
        // surrogate pair D83D DE00. Place it across the truncation boundary:
        // "abc" (3 BMP chars) + "😀" (2 surrogate code units) = length 5.
        var input = "abc😀";
        var result = ServiceConnectActivitySource.Truncate(input, maxLength: 4);

        // A naive slice returns "abc\uD83D" — an orphaned high surrogate.
        // The corrected implementation returns "abc" — trimmed one extra char.
        Assert.Equal("abc", result);
        foreach (var c in result)
        {
            Assert.False(char.IsHighSurrogate(c) || char.IsLowSurrogate(c),
                $"Found orphan surrogate U+{(int)c:X4}");
        }
    }

    [Fact]
    public void Truncate_AtNonSurrogateBoundary_KeepsAllValidChars()
    {
        var result = ServiceConnectActivitySource.Truncate("abcdef", maxLength: 3);
        Assert.Equal("abc", result);
    }

    [Fact]
    public void Truncate_ShortString_ReturnsUnchanged()
    {
        var result = ServiceConnectActivitySource.Truncate("hi", maxLength: 10);
        Assert.Equal("hi", result);
    }

    [Fact]
    public void Truncate_Null_ReturnsEmpty()
    {
        var result = ServiceConnectActivitySource.Truncate(null, maxLength: 10);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Truncate_MaxLengthZero_ReturnsValueUnchanged()
    {
        // maxLength <= 0 is treated as "no limit" — the full value is returned.
        var result = ServiceConnectActivitySource.Truncate("abc", maxLength: 0);
        Assert.Equal("abc", result);
    }

    [Fact]
    public void Truncate_ExactLength_ReturnsUnchanged()
    {
        var result = ServiceConnectActivitySource.Truncate("abc", maxLength: 3);
        Assert.Equal("abc", result);
    }

    [Fact]
    public void Truncate_SurrogatePairFitsEntirely_ReturnsBothCodeUnits()
    {
        // "ab😀" has length 4; maxLength 4 means no truncation needed.
        var result = ServiceConnectActivitySource.Truncate("ab😀", maxLength: 4);
        Assert.Equal("ab😀", result);
    }

    [Fact]
    public void Truncate_LowSurrogateAtBoundary_IsNotOrphaned()
    {
        // If by some construction a low surrogate ends up at position maxLength-1
        // (paired with a high surrogate two positions before it), the slice would
        // keep the low surrogate — which is also orphaned. This tests that the
        // implementation at least never splits a well-formed pair on entry.
        // "a😀b" = 'a'(0), \uD83D(1), \uDE00(2), 'b'(3); maxLength=2 cuts at index 2.
        var input = "a😀b";
        var result = ServiceConnectActivitySource.Truncate(input, maxLength: 2);

        // Position 1 is the high surrogate — must be trimmed, leaving "a".
        Assert.Equal("a", result);
        foreach (var c in result)
        {
            Assert.False(char.IsHighSurrogate(c) || char.IsLowSurrogate(c),
                $"Found orphan surrogate U+{(int)c:X4}");
        }
    }
}
