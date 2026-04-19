using System.Runtime.CompilerServices;
using System.Text;

namespace ServiceConnect.Interfaces;

/// <summary>
/// Converts transport header values into their string representation.
/// </summary>
public static class HeaderDecoder
{
    /// <summary>
    /// Decodes a header value that is stored as either a UTF-8 byte array or a string.
    /// </summary>
    /// <param name="value">The raw header value.</param>
    /// <returns>The decoded string, or <see langword="null"/> when the value is <see langword="null"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? Decode(object? value)
    {
        if (value is null) return null;
        if (value is byte[] bytes) return Encoding.UTF8.GetString(bytes);
        if (value is string s) return s;
        throw new ArgumentException(
            $"Unexpected header value type: {value.GetType().FullName}",
            nameof(value));
    }
}
