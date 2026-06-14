using ServiceConnect.Examples.PointToPoint.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.PointToPoint.Consumer;

public sealed class WorkSubmittedHandler : IMessageHandler<WorkSubmitted>
{
    public Task HandleAsync(WorkSubmitted message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        ConsoleStatus.Success("point-to-point-consumer", $"processed {message.WorkId}");
        return Task.CompletedTask;
    }
}
