using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class MessageTypeExchangeNameVersionStableTests
{
    public sealed class TypeInThisAssembly { }

    [Fact]
    public void From_DependsOnlyOnFullName_NotAssemblyVersionOrQualifiedName()
    {
        // The exchange name is derived purely from Type.FullName (dots removed), so it is
        // inherently stable across assembly-version bumps and carries no version/culture/PKT
        // metadata. Producers and consumers built against different assembly versions of the
        // same logical type therefore derive an identical name.
        var type = typeof(TypeInThisAssembly);
        var expected = type.FullName!.Replace(".", string.Empty);

        var actual = MessageTypeExchangeName.From(type);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("Version=", actual);
        Assert.DoesNotContain("Culture=", actual);
    }
}
