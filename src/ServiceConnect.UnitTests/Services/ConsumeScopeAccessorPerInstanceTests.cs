using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class ConsumeScopeAccessorPerInstanceTests
{
    [Fact]
    public void TwoInstances_DoNotShareScope()
    {
        var accessorA = new ConsumeScopeAccessor();
        var accessorB = new ConsumeScopeAccessor();
        var providerA = new ServiceCollection().BuildServiceProvider();

        using (accessorA.Push(providerA))
        {
            // accessorB.Current must throw — its scope was not pushed.
            Assert.Throws<InvalidOperationException>(() => accessorB.Current);
            Assert.Same(providerA, accessorA.Current);
        }
    }

    [Fact]
    public async Task ConcurrentDispatches_AcrossInstances_DoNotBleed()
    {
        var accessorA = new ConsumeScopeAccessor();
        var accessorB = new ConsumeScopeAccessor();
        var providerA = new ServiceCollection().BuildServiceProvider();
        var providerB = new ServiceCollection().BuildServiceProvider();

        var taskA = Task.Run(async () =>
        {
            using (accessorA.Push(providerA))
            {
                await Task.Yield();
                Assert.Same(providerA, accessorA.Current);
                Assert.Throws<InvalidOperationException>(() => accessorB.Current);
            }
        });
        var taskB = Task.Run(async () =>
        {
            using (accessorB.Push(providerB))
            {
                await Task.Yield();
                Assert.Same(providerB, accessorB.Current);
                Assert.Throws<InvalidOperationException>(() => accessorA.Current);
            }
        });

        await Task.WhenAll(taskA, taskB);
    }
}
