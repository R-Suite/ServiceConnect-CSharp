using System.Diagnostics;
using ServiceConnect.Telemetry;

namespace ServiceConnect.Examples.Telemetry.AnalyticsSubscriber;

internal static class TelemetryConsoleListener
{
    public static void Register(string endpoint)
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = src =>
                src.Name == ServiceConnectActivitySource.PublishActivitySourceName ||
                src.Name == ServiceConnectActivitySource.SendActivitySourceName ||
                src.Name == ServiceConnectActivitySource.ConsumeActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => Console.WriteLine(
                $"TRACE:{endpoint}:{a.OperationName}:{a.TraceId}:{a.SpanId}:{a.ParentSpanId}"),
        });
    }
}
