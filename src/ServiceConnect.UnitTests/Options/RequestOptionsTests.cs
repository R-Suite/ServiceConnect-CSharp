using System.Linq;
using System.Reflection;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.UnitTests.Options;

public class RequestOptionsShapeTests
{
    [Fact]
    public void RequestOptions_IsReadonlyRecordStruct()
    {
        var t = typeof(RequestOptions);
        Assert.True(t.IsValueType, "RequestOptions must be a value type (record struct).");
        Assert.True(
            t.GetCustomAttributesData().Any(a => a.AttributeType.Name == "IsReadOnlyAttribute"),
            "RequestOptions must be declared 'readonly'.");
    }

    [Fact]
    public void RequestOptions_AllSettersAreInitOnly()
    {
        foreach (var p in typeof(RequestOptions).GetProperties())
        {
            var setter = p.GetSetMethod(nonPublic: true);
            if (setter is null)
            {
                continue;
            }
            // init-only setters carry the IsExternalInit modreq.
            Assert.Contains(
                setter.ReturnParameter.GetRequiredCustomModifiers(),
                m => m.Name == "IsExternalInit");
        }
    }

    [Fact]
    public void RequestOptions_Default_HasDefaultTimeout()
    {
        Assert.Equal(RequestOptions.DefaultTimeoutMs, RequestOptions.Default.Timeout);
    }

    [Fact]
    public void Default_HasNonZeroTimeout()
    {
        Assert.Equal(RequestOptions.DefaultTimeoutMs, RequestOptions.Default.Timeout);
        Assert.True(RequestOptions.Default.Timeout > 0);
    }

    [Fact]
    public void DefaultStruct_HasZeroTimeout_DocumentingTheTrap()
    {
        // Documents the language-level behaviour the ValidateOptions guard exists to catch:
        // default(RequestOptions) skips the parameterless ctor and leaves Timeout=0.
#pragma warning disable IDE0034 // explicit form documents the default(T) trap intentionally
        Assert.Equal(0, default(RequestOptions).Timeout);
#pragma warning restore IDE0034
    }
}
