using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Events;

public class ConsumeEventArgsHeadersTests
{
    [Fact]
    public void Headers_RepeatedReadsReturnSameReference()
    {
        var args = new ConsumeEventArgs
        {
            Headers = new Dictionary<string, object> { ["k"] = "v" },
        };

        var read1 = args.Headers;
        var read2 = args.Headers;
        Assert.Same(read1, read2);
    }

    [Fact]
    public void Headers_NotInitialised_IsNotPermitted()
    {
        // Constructing without Headers must either default to a usable empty dictionary
        // or throw — what it must NOT do is hand out a different reference per call.
        var args = new ConsumeEventArgs { Message = [1] };
        var read1 = args.Headers;
        var read2 = args.Headers;
        Assert.Same(read1, read2);
    }
}
