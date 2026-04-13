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

    // Keep the error-queue header bounded in both breadth and depth so that arbitrary
    // inner-exception chains (including ones that might reveal connection strings or
    // file paths) cannot bloat the message or leak beyond a controlled surface (S-07).
    private const int MaxErrorMessageInnerDepth = 3;
    private const int MaxErrorMessageLength = 2048;

    public static string GetErrorMessage(Exception exception)
    {
        var sb = new StringBuilder();
        sb.AppendLine(exception.Message);
        var ie = exception.InnerException;
        int depth = 0;
        while (ie != null && depth < MaxErrorMessageInnerDepth)
        {
            sb.AppendLine(ie.Message);
            ie = ie.InnerException;
            depth++;
        }
        var s = sb.ToString();
        return s.Length <= MaxErrorMessageLength ? s : s[..MaxErrorMessageLength];
    }
}
