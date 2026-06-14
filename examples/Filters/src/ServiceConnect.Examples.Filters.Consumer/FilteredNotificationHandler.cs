using ServiceConnect.Examples.Filters.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.Filters.Consumer;

public sealed class FilteredNotificationHandler : IMessageHandler<FilteredNotification>
{
    public Task HandleAsync(FilteredNotification message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        var traceId = context.Headers.TryGetValue("X-Trace-Id", out var rawTraceId)
            ? HeaderDecoder.Decode(rawTraceId) ?? "missing"
            : "missing";

        ConsoleStatus.Success("filters-consumer", $"trace {traceId}");
        return Task.CompletedTask;
    }
}
