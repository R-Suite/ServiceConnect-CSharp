using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Messages;

public class MessageInitTests
{
    [Fact]
    public void CorrelationId_HasInitAccessor_NotPrivateSet()
    {
        // Compile-time guard: the setter must be init-only.
        var prop = typeof(Message).GetProperty(nameof(Message.CorrelationId));
        Assert.NotNull(prop);
        var setter = prop!.SetMethod!;
        Assert.True(setter.ReturnParameter.GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit"),
            "Message.CorrelationId setter must be init-only (System.Runtime.CompilerServices.IsExternalInit modreq).");
    }

    [Fact]
    public void Message_ConstructionAssignsCorrelationId()
    {
        var id = Guid.NewGuid();
        var msg = new Message(id);
        Assert.Equal(id, msg.CorrelationId);
    }
}
