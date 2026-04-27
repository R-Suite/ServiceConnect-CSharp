using System.Runtime.CompilerServices;
using System.Text;

namespace ServiceConnect.Client.RabbitMQ;

internal static class HeaderHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetHeader<T>(IDictionary<string, object> headers, string key, T value)
    {
        if (value is null)
        {
            _ = headers.Remove(key);
        }
        else
        {
            headers[key] = value;
        }
    }

    // foreach into a pre-sized dictionary avoids the LINQ ToDictionary allocation overhead.
    public static Dictionary<string, object?> ToNullableHeaders(IDictionary<string, object> headers)
    {
        var result = new Dictionary<string, object?>(headers.Count, StringComparer.Ordinal);
        foreach (var kvp in headers)
        {
            result[kvp.Key] = kvp.Value;
        }

        return result;
    }

    // Keep the error-queue header bounded in both breadth and depth so that arbitrary
    // inner-exception chains (including ones that might reveal connection strings or
    // file paths) cannot bloat the message or leak beyond a controlled surface.
    private const int MaxErrorMessageInnerDepth = 3;
    private const int MaxErrorMessageLength = 4096;
    private const string TruncationMarker = "...[truncated]";

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
        // Append a marker when the chain was deeper than we recorded so operators
        // know to check logs for the full inner-exception stack.
        if (ie != null)
        {
            sb.Append(TruncationMarker);
        }

        var s = sb.ToString();
        if (s.Length <= MaxErrorMessageLength)
        {
            return s;
        }

        return string.Concat(s.AsSpan(0, MaxErrorMessageLength - TruncationMarker.Length), TruncationMarker);
    }
}
