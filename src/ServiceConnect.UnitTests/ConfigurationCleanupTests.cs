using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

public class ConfigurationCleanupTests
{
    [Fact]
    public void OutgoingEventArgs_HeadersSetter_RejectsNull()
    {
        var args = new OutgoingEventArgs();

        Assert.Throws<ArgumentNullException>(() => args.Headers = null!);
    }

    [Fact]
    public void SendEventArgs_EndPointAndEndPoints_AreIndependent()
    {
        var args = new SendEventArgs
        {
            EndPoint = "queue-a",
            EndPoints = ["queue-b", "queue-c"]
        };

        Assert.Equal("queue-a", args.EndPoint);
        Assert.Equal(["queue-b", "queue-c"], args.EndPoints);
    }
}
