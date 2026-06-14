namespace ServiceConnect.Services;

/// <summary>
/// Derives transport-safe exchange and binding names from message-type metadata.
/// Producers and consumers must agree on this mapping so binding names match declared
/// exchanges — sharing the helper keeps them in lock-step.
/// </summary>
/// <remarks>
/// This is part of the public API surface because adapter packages (e.g.
/// <c>ServiceConnect.Client.RabbitMQ</c>) need to derive the same name as the core bus.
/// The mapping is the C# <c>master</c> wire convention <c>Type.FullName.Replace(".", "")</c>,
/// shared with the deployed .NET <c>master</c> services and the Node.js implementation so all
/// three interoperate on the same exchanges. The algorithm is frozen for wire compatibility.
/// </remarks>
public static class MessageTypeExchangeName
{
    /// <summary>
    /// Computes the deterministic exchange / binding name for the given message type:
    /// its <see cref="Type.FullName"/> with the namespace dots removed.
    /// </summary>
    /// <param name="type">The CLR type whose name is being mapped. Must have a non-null <see cref="Type.FullName"/>.</param>
    /// <returns>
    /// The full type name with every <c>.</c> removed, e.g. <c>MyApp.Messages.OrderPlaced</c>
    /// becomes <c>MyAppMessagesOrderPlaced</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="type"/> has no <see cref="Type.FullName"/>.</exception>
    // Matches master's `type.FullName.Replace(".", string.Empty)` exactly. The mapping is not
    // injective ("A.BC" and "AB.C" both flatten to "ABC"), but master is the canonical wire
    // format and accepts that, so the C# and Node implementations align to it rather than
    // disambiguating with a hash suffix that master and Node do not share.
    public static string From(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var full = type.FullName
            ?? throw new ArgumentException($"Type '{type}' has no FullName.", nameof(type));

        return full.Replace(".", string.Empty);
    }
}
