using System.Reflection;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

public class HandlerContextNullabilityTests
{
    // Context is a public part of the handler contract and the dispatch pipeline
    // always assigns it before HandleAsync runs. These tests pin the non-nullable
    // annotation via the runtime NullabilityInfoContext so handlers can rely on
    // Context without defensive `!` or null checks, and any change that flips
    // the annotation back to nullable will fail here.

    [Fact]
    public void IMessageHandler_Context_IsNonNullable()
    {
        PropertyInfo property = typeof(IMessageHandler<>).GetProperty(nameof(IMessageHandler<Message>.Context))!;
        NullabilityInfo info = new NullabilityInfoContext().Create(property);

        Assert.Equal(NullabilityState.NotNull, info.ReadState);
        Assert.Equal(NullabilityState.NotNull, info.WriteState);
    }

    [Fact]
    public void IProcessHandler_Context_IsNonNullable()
    {
        PropertyInfo property = typeof(IProcessHandler<,>).GetProperty(nameof(IProcessHandler<DummyData, Message>.Context))!;
        NullabilityInfo info = new NullabilityInfoContext().Create(property);

        Assert.Equal(NullabilityState.NotNull, info.ReadState);
        Assert.Equal(NullabilityState.NotNull, info.WriteState);
    }

    private sealed class DummyData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public int Version { get; set; }
    }
}
