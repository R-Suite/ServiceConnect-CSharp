using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class MessageTypeExchangeNameVersionStableTests
{
    public sealed class TypeInThisAssembly { }

    [Fact]
    public void From_TypeName_DependsOnFullNameAndAssemblyName_NotVersion()
    {
        // Prove the implementation uses FullName + ", " + Assembly.GetName().Name
        // (not AssemblyQualifiedName which includes version/culture/PKT). Compute the
        // expected hash directly from that suffix source and assert equality.
        var type = typeof(TypeInThisAssembly);
        var expected = ComputeExpected(type);

        var actual = MessageTypeExchangeName.From(type);

        Assert.Equal(expected, actual);
    }

    private static string ComputeExpected(Type type)
    {
        var sanitized = type.FullName!.Replace(".", string.Empty);
        var suffixSource = $"{type.FullName}, {type.Assembly.GetName().Name}";
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(suffixSource), hash);
        var sb = new System.Text.StringBuilder(sanitized.Length + 1 + 8);
        sb.Append(sanitized).Append('_');
        for (var i = 0; i < 4; i++)
        {
            sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
