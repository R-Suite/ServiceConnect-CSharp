using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ServiceConnect.Services;

/// <summary>
/// Derives transport-safe exchange and binding names from message-type metadata.
/// Producers and consumers must agree on this mapping so binding names match declared
/// exchanges — sharing the helper keeps them in lock-step.
/// </summary>
/// <remarks>
/// This is part of the public API surface because adapter packages (e.g.
/// <c>ServiceConnect.Client.RabbitMQ</c>) need to derive the same name as the core bus.
/// The output format is wire-compatibility-stable: changing how the name is computed
/// would silently re-route messages across deployed services that pinned different
/// versions of the core and adapter packages, so the algorithm is fixed.
/// </remarks>
public static class MessageTypeExchangeName
{
    /// <summary>
    /// Computes the deterministic exchange / binding name for the given message type.
    /// </summary>
    /// <param name="type">The CLR type whose name is being mapped. Must have a non-null <see cref="Type.FullName"/>.</param>
    /// <returns>
    /// The flattened type name (dots stripped) suffixed with an underscore and an
    /// eight-hex-character SHA-256 prefix derived from the full type name plus the
    /// assembly simple name (version-stable). Pre-v8 the suffix was derived from
    /// the assembly-qualified name and changed across version bumps.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="type"/> has no <see cref="Type.FullName"/>.</exception>
    // FullName.Replace(".", "") alone is not injective — "A.BC" and "AB.C" both
    // flatten to "ABC" and would share an exchange, cross-wiring routing. The
    // eight-char hash suffix keeps the mapping unique across colliding flattened
    // names while staying short enough to fit inside AMQP identifier length limits.
    public static string From(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var full = type.FullName
            ?? throw new ArgumentException($"Type '{type}' has no FullName.", nameof(type));
        var sanitized = full.Replace(".", string.Empty);

        // Drop AssemblyQualifiedName (which includes version + culture + PKT) so producers
        // and consumers built against different assembly versions of the same logical type
        // derive the same exchange name. Falls back to FullName alone if Assembly metadata
        // is unavailable.
        var assemblyName = type.Assembly.GetName().Name;
        var suffixSource = assemblyName is null ? full : $"{full}, {assemblyName}";

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
