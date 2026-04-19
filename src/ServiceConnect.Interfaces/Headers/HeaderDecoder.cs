using System.Runtime.CompilerServices;
using System.Text;

namespace ServiceConnect.Interfaces;

public static class HeaderDecoder
{
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
