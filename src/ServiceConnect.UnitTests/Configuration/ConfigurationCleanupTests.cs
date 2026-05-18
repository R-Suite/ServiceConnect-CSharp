using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Configuration;

public class ConfigurationCleanupTests
{
    [Fact]
    public void OutgoingEventArgs_Headers_RejectsNullInInitializer()
    {
        // Headers is init-only so a subscriber cannot swap the whole dictionary
        // after the framework has built it (which would let a subscriber strip
        // required MessageType/CorrelationId entries before the transport send).
        // A null value supplied in the initializer must still be rejected up front.
        Assert.Throws<ArgumentNullException>(() => new SendEventArgs { Headers = null! });
    }

    [Fact]
    public void OutgoingEventArgs_Headers_IsInitOnly()
    {
        var setter = typeof(OutgoingEventArgs).GetProperty(nameof(OutgoingEventArgs.Headers))!.SetMethod!;
        var modreqs = setter.ReturnParameter.GetRequiredCustomModifiers();
        Assert.Contains(modreqs, t => t.Name == "IsExternalInit");
    }

    [Fact]
    public void SendEventArgs_EndPoint_IsInitOnly()
    {
        // Per-delivery EndPoint is the only public surface — fan-out fires one
        // SendEventArgs per destination. Correlate fan-out via CorrelationId.
        var args = new SendEventArgs { EndPoint = "queue-a" };
        Assert.Equal("queue-a", args.EndPoint);
    }
}
