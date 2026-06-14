using ServiceConnect.Examples.CompetingConsumers.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.CompetingConsumers.WorkerA;

public sealed class JobQueuedHandler : IMessageHandler<JobQueued>
{
    public async Task HandleAsync(JobQueued message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        ConsoleStatus.Success("worker-a", $"processed {message.JobId}");
        await Console.Out.FlushAsync();
    }
}
