using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace ServiceConnect.Filters.MessageDeduplication
{
    // Placeholder — Task 5 implements the cleanup loop.
    public sealed class DeduplicationCleanupHostedService : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
}
