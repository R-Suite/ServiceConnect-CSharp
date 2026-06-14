using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.MongoDb;

/// <summary>
/// Pre-creates per-saga unique CorrelationId indexes at startup. Closes the
/// cross-process race window where two cold-started processes could insert
/// duplicate saga rows before either one called the lazy index-creation path.
/// The lazy fallback in <see cref="MongoDbProcessManagerFinder"/> is retained
/// so a startup failure (transient connectivity, auth flap) does not wedge
/// the process — it just retries on first I/O.
/// </summary>
internal sealed class MongoDbProcessManagerIndexInitializer(
    MongoDbProcessManagerFinder finder,
    IProcessManagerTypeRegistry registry,
    ILogger<MongoDbProcessManagerIndexInitializer> logger) : IHostedService
{
    private readonly MongoDbProcessManagerFinder _finder = finder ?? throw new ArgumentNullException(nameof(finder));
    private readonly IProcessManagerTypeRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly ILogger<MongoDbProcessManagerIndexInitializer> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var dataType in _registry.SagaDataTypes)
        {
            try
            {
                await _finder.EnsureCorrelationIdIndexForTypeAsync(dataType, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Lazy fallback in the I/O path will retry. We log a warning so the
                // operator notices the startup miss but the bus boots and runs.
                _logger.LogWarning(ex,
                    "Failed to pre-create CorrelationId index for {SagaDataType}; lazy fallback will retry on first I/O.",
                    dataType.FullName);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
