using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.DependencyInjection;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.DI;

public class OnConsumedSuccessfullyFiltersValidationTests
{
    public sealed class UnregisteredFilter : IFilter
    {
        public Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
            => Task.FromResult(FilterAction.Continue);
    }

    [Fact]
    public void AddServiceConnect_FailsAtStartup_WhenOnConsumedSuccessfullyFilterIsNotRegistered()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            services.AddServiceConnect(b =>
            {
                b.ConfigureQueues(q => q.QueueName = "test");
                b.AddOnConsumedSuccessfullyFilter<UnregisteredFilter>();
            });
        });

        Assert.Contains(typeof(UnregisteredFilter).FullName!, ex.Message);
        Assert.Contains("not registered", ex.Message);
    }
}
