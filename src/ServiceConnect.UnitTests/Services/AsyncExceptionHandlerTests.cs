using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class AsyncExceptionHandlerTests
{
    [Fact]
    public async Task ExceptionHandler_AsyncSignature_AwaitsHandler()
    {
        var observed = new List<Exception>();
        var cfg = new BusConfiguration
        {
            ExceptionHandler = async (ex, ct) =>
            {
                await Task.Yield();
                observed.Add(ex);
            }
        };

        // Act
        var failure = new InvalidOperationException("test-failure");
        await cfg.ExceptionHandler!.Invoke(failure, CancellationToken.None);

        Assert.Single(observed);
        Assert.Same(failure, observed[0]);
    }

    [Fact]
    public async Task ExceptionHandler_SyncShim_RunsAndReturnsCompletedValueTask()
    {
        // Compile-time and runtime regression guard for the sync-shim pattern
        // documented on IBusConfiguration.ExceptionHandler: a synchronous body can be
        // wrapped to satisfy the (Exception, CancellationToken) → ValueTask signature.
        var cfg = new BusConfiguration();
        var ran = false;
        cfg.ExceptionHandler = (ex, _) =>
        {
            ran = true;
            return ValueTask.CompletedTask;
        };

        await cfg.ExceptionHandler!.Invoke(new Exception(), CancellationToken.None);

        Assert.True(ran, "Sync shim must execute the wrapped action.");
    }
}
