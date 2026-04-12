using System.Text;

namespace ServiceConnect.Interfaces;

public static class HeaderDecoder
{
    public static string? Decode(object? value)
    {
        if (value is byte[] bytes)
            return Encoding.UTF8.GetString(bytes);
        return value?.ToString();
    }
}
