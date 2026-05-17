using System;
using ServiceConnect.Services.Processors;
using Xunit;

public class ProcessManagerProcessorReferenceEqualityTests
{
    private sealed class RefTypeNoEqualsOverride
    {
        public int Value { get; init; }
    }

    private class RefTypeWithEqualsOverride
    {
        public int Value { get; init; }
        public override bool Equals(object? obj) => obj is RefTypeWithEqualsOverride o && o.Value == Value;
        public override int GetHashCode() => Value.GetHashCode();
    }

    private sealed class InheritsEqualsFromBase : RefTypeWithEqualsOverride { }

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(RefTypeWithEqualsOverride))]
    [InlineData(typeof(DayOfWeek))]
    [InlineData(typeof(int?))]
    [InlineData(typeof(TimeSpan))]
    [InlineData(typeof(InheritsEqualsFromBase))]
    public void IsValueEqualType_ValueEqualTypes_ReturnsTrue(Type t)
        => Assert.True(ProcessManagerProcessor.IsValueEqualType(t));

    [Theory]
    [InlineData(typeof(byte[]))]
    [InlineData(typeof(int[]))]
    [InlineData(typeof(RefTypeNoEqualsOverride))]
    [InlineData(typeof(object))]
    [InlineData(typeof(System.Collections.Generic.List<int>))]
    public void IsValueEqualType_ReferenceEqualityTypes_ReturnsFalse(Type t)
        => Assert.False(ProcessManagerProcessor.IsValueEqualType(t));
}
