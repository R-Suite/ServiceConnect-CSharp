using System.Diagnostics;

namespace ServiceConnect.Examples.Telemetry.BillingSubscriber;

internal static class TelemetryConsoleListener
{
    public static void Register(string endpoint)
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = src => src.Name.StartsWith("ServiceConnect.Bus.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => Console.WriteLine(
                $"TRACE:{endpoint}:{a.OperationName}:{a.TraceId}:{a.SpanId}:{a.ParentSpanId}"),
        });
    }
}
