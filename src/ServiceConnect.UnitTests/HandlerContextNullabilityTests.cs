using System.Reflection;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

public class HandlerContextNullabilityTests
{
    // Before M8, both interfaces declared `Context` as `IConsumeContext?`. The
    // dispatch pipeline *always* sets Context before HandleAsync, so the null
    // annotation forced every NRT-aware handler to sprinkle `!` or defensive
    // null checks. These tests lock in the non-nullable contract via the
    // runtime NullabilityInfoContext — a signature regression (someone
    // re-adding `?`) would flip Nullable from NotNull to Nullable and fail.

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
