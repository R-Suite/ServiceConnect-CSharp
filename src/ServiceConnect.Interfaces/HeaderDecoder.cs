using System.Runtime.CompilerServices;
using System.Text;
using System.Diagnostics;

namespace ServiceConnect.Interfaces;

public static class HeaderDecoder
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? Decode(object? value)
    {
        if (value is byte[] bytes)
            return Encoding.UTF8.GetString(bytes);
        Debug.Assert(value is null or string, $"Unexpected header value type: {value?.GetType().FullName}");
        return value?.ToString();
    }
}
