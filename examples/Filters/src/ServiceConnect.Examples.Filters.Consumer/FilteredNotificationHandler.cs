using ServiceConnect.Examples.Filters.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.Filters.Consumer;

public sealed class FilteredNotificationHandler : IMessageHandler<FilteredNotification>
{
    public IConsumeContext Context { get; set; } = null!;

    public Task HandleAsync(FilteredNotification message)
    {
        var traceId = Context is not null && Context.Headers.TryGetValue("X-Trace-Id", out var rawTraceId)
            ? HeaderDecoder.Decode(rawTraceId) ?? "missing"
            : "missing";

        ConsoleStatus.Success("filters-consumer", $"trace {traceId}");
        return Task.CompletedTask;
    }
}
