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
}
