using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.UnitTests.Options;

public class SendOptionsShapeTests
{
    [Fact]
    public void EndPoint_IsNullableString()
    {
        // EndPoints (plural) was removed; single-destination routing uses EndPoint.
        // Fan-out callers must use IBus.SendToManyAsync instead.
        Assert.Equal(
            typeof(string),
            Nullable.GetUnderlyingType(typeof(SendOptions).GetProperty("EndPoint")!.PropertyType)
            ?? typeof(SendOptions).GetProperty("EndPoint")!.PropertyType);
    }

    [Fact]
    public void EndPoints_PropertyDoesNotExist()
    {
        // SendOptions.EndPoints was removed in favour of IBus.SendToManyAsync.
        Assert.Null(typeof(SendOptions).GetProperty("EndPoints"));
    }
}
