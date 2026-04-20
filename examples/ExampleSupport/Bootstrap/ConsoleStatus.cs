namespace ServiceConnect.Examples.Support.Bootstrap;

public static class ConsoleStatus
{
    public static void Ready(string endpointName) => Console.WriteLine($"READY:{Sanitize(endpointName)}");

    public static void Success(string endpointName, string detail) =>
        Console.WriteLine($"SUCCESS:{Sanitize(endpointName)}:{Sanitize(detail)}");

    public static void Error(string endpointName, Exception exception) =>
        Console.WriteLine($"ERROR:{Sanitize(endpointName)}:{Sanitize(exception.Message)}");

    private static string Sanitize(string value)
    {
        return value
            .Replace(':', ';')
            .Replace("\r", " ")
            .Replace("\n", " ");
    }
}
