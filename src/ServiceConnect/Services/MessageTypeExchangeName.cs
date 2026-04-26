using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ServiceConnect.Services;

/// <summary>
/// Derives transport-safe exchange and binding names from message-type metadata.
/// Producers and consumers must agree on this mapping so binding names match
/// declared exchanges — sharing the helper keeps them in lock-step.
/// </summary>
internal static class MessageTypeExchangeName
{
    // FullName.Replace(".", "") alone is not injective — "A.BC" and "AB.C" both
    // flatten to "ABC" and would share an exchange, cross-wiring routing. The
    // eight-char hash suffix, drawn from the assembly-qualified name, keeps the
    // mapping unique across colliding flattened names while staying short enough
    // to fit inside AMQP identifier length limits.
    public static string From(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var full = type.FullName
            ?? throw new ArgumentException($"Type '{type}' has no FullName.", nameof(type));
        var sanitized = full.Replace(".", string.Empty);
        var suffixSource = type.AssemblyQualifiedName ?? full;

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(suffixSource), hash);

        var builder = new StringBuilder(sanitized.Length + 1 + 8);
        builder.Append(sanitized);
        builder.Append('_');
        for (int i = 0; i < 4; i++)
        {
            builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
