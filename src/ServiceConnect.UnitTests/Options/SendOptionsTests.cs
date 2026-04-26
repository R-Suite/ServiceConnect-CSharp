using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.UnitTests.Options;

public class SendOptionsShapeTests
{
    [Fact]
    public void EndPoints_IsReadOnlyList()
    {
        Assert.Equal(
            typeof(IReadOnlyList<string>),
            typeof(SendOptions).GetProperty("EndPoints")!.PropertyType);
    }
}
