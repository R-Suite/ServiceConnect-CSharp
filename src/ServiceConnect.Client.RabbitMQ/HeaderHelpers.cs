using System.Text;

namespace ServiceConnect.Client.RabbitMQ;

internal static class HeaderHelpers
{
    public static void SetHeader<T>(IDictionary<string, object> headers, string key, T value)
    {
        if (value is null) _ = headers.Remove(key);
        else headers[key] = value;
    }

    public static Dictionary<string, object?> ToNullableHeaders(IDictionary<string, object> headers)
        => headers.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);

    public static string GetErrorMessage(Exception exception)
    {
        var sb = new StringBuilder();
        sb.AppendLine(exception.Message);
        var ie = exception.InnerException;
        while (ie != null)
        {
            sb.AppendLine(ie.Message);
            ie = ie.InnerException;
        }
        return sb.ToString();
    }
}
