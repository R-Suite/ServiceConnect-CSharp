using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class MessageBusReadStreamGuardTests
{
    [Fact]
    public void Write_NullData_ThrowsArgumentNullException()
    {
        var stream = new MessageBusReadStream("seq");
        Assert.Throws<ArgumentNullException>(() => stream.Write(data: null!, packetNumber: 0));
    }
}
