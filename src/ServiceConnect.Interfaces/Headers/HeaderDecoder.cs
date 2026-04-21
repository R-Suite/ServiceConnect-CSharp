using System.Runtime.CompilerServices;
using System.Text;

namespace ServiceConnect.Interfaces;

/// <summary>
/// Converts transport header values into their string representation.
/// </summary>
public static class HeaderDecoder
{
    /// <summary>
    /// Decodes a header value to its string representation. Handles the two canonical
    /// wire shapes (UTF-8 <see cref="byte"/>[] from RabbitMQ clients; <see cref="string"/>
    /// from in-process paths) and falls back to <see cref="object.ToString"/> for any other
    /// AMQP-native type (e.g. <c>long</c>, <c>short</c>, <c>IDictionary</c>). This must
    /// never throw: a throw here bubbles up to the consumer host, which nacks with
    /// <c>requeue:true</c> and infinitely redelivers the poison message.
    /// </summary>
    /// <param name="value">The raw header value.</param>
    /// <returns>The decoded string, or <see langword="null"/> when the value is <see langword="null"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? Decode(object? value)
    {
        if (value is null) return null;
        if (value is byte[] bytes) return Encoding.UTF8.GetString(bytes);
        if (value is string s) return s;
        return value.ToString();
    }
}
