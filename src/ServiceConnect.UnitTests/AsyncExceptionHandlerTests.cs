using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.Configuration;

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
    public async Task ExceptionHandler_SyncShim_Compiles()
    {
        // Compile-time regression guard: the v7-style sync shim must remain valid for v8.
        var cfg = new BusConfiguration
        {
            // Simulates how callers migrating from Action<Exception> would wrap sync code.
            ExceptionHandler = (ex, _) => { /* Log.Error(ex); */ return ValueTask.CompletedTask; }
        };

        await cfg.ExceptionHandler!.Invoke(new Exception(), CancellationToken.None);
    }
}
