using ServiceConnect.Examples.StressHarness.Reporting;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Reporting;

public class BytesTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1_048_575, "1024.0 KB")]
    [InlineData(1_048_576, "1.0 MB")]
    [InlineData(1_572_864, "1.5 MB")]
    [InlineData(50L * 1024 * 1024, "50.0 MB")]
    [InlineData(1024L * 1024 * 1024, "1.00 GB")]
    [InlineData(1536L * 1024 * 1024, "1.50 GB")]
    public void Format_Boundaries(long bytes, string expected)
    {
        Assert.Equal(expected, Bytes.Format(bytes));
    }
}
