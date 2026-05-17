using System;
using ServiceConnect.Services.Processors;
using Xunit;

public class ProcessManagerProcessorReferenceEqualityTests
{
    private sealed class RefTypeNoEqualsOverride
    {
        public int Value { get; init; }
    }

    private sealed class RefTypeWithEqualsOverride
    {
        public int Value { get; init; }
        public override bool Equals(object? obj) => obj is RefTypeWithEqualsOverride o && o.Value == Value;
        public override int GetHashCode() => Value.GetHashCode();
    }

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(RefTypeWithEqualsOverride))]
    public void IsValueEqualType_ValueEqualTypes_ReturnsTrue(Type t)
        => Assert.True(ProcessManagerProcessor.IsValueEqualType(t));

    [Theory]
    [InlineData(typeof(byte[]))]
    [InlineData(typeof(int[]))]
    [InlineData(typeof(RefTypeNoEqualsOverride))]
    [InlineData(typeof(object))]
    public void IsValueEqualType_ReferenceEqualityTypes_ReturnsFalse(Type t)
        => Assert.False(ProcessManagerProcessor.IsValueEqualType(t));
}
