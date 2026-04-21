using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

public class ConfigurationCleanupTests
{
    [Fact]
    public void OutgoingEventArgs_Headers_RejectsNullInInitializer()
    {
        // M11: Headers is init-only so a subscriber can't swap the whole
        // dictionary after the framework built it (and thus cannot strip
        // required MessageType/CorrelationId entries before transport send).
        // Null in the initializer is still rejected.
        Assert.Throws<ArgumentNullException>(() => new OutgoingEventArgs { Headers = null! });
    }

    [Fact]
    public void OutgoingEventArgs_Headers_IsInitOnly()
    {
        var setter = typeof(OutgoingEventArgs).GetProperty(nameof(OutgoingEventArgs.Headers))!.SetMethod!;
        var modreqs = setter.ReturnParameter.GetRequiredCustomModifiers();
        Assert.Contains(modreqs, t => t.Name == "IsExternalInit");
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
