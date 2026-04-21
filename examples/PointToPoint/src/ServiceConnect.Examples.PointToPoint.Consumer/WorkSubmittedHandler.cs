using ServiceConnect.Examples.PointToPoint.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.PointToPoint.Consumer;

public sealed class WorkSubmittedHandler : IMessageHandler<WorkSubmitted>
{
    public IConsumeContext Context { get; set; } = null!;

    public Task HandleAsync(WorkSubmitted message)
    {
        ConsoleStatus.Success("point-to-point-consumer", $"processed {message.WorkId}");
        return Task.CompletedTask;
    }
}
