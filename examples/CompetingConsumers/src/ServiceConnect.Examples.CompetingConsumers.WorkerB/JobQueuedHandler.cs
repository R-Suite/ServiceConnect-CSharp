using ServiceConnect.Examples.CompetingConsumers.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.CompetingConsumers.WorkerB;

public sealed class JobQueuedHandler : IMessageHandler<JobQueued>
{
    public IConsumeContext Context { get; set; } = null!;

    public async Task HandleAsync(JobQueued message, CancellationToken cancellationToken = default)
    {
        ConsoleStatus.Success("worker-b", $"processed {message.JobId}");
        await Console.Out.FlushAsync();
    }
}
