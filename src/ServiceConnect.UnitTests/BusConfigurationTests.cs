using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public class BusConfigurationTests
{
    [Fact]
    public void BusConfiguration_HasCorrectDefaults()
    {
        var config = new BusConfiguration();

        Assert.True(config.ScanForMessageHandlers);
        Assert.True(config.AutoStartConsuming);
        Assert.False(config.EnableProcessManagerTimeouts);
        Assert.Equal(1, config.ConsumerCount);
        Assert.Null(config.ExceptionHandler);
    }

    [Fact]
    public void PipelineConfiguration_StartsWithEmptyLists()
    {
        var config = new BusConfiguration();
        var pipeline = config.Pipeline;

        Assert.Empty(pipeline.BeforeConsumingFilters);
        Assert.Empty(pipeline.AfterConsumingFilters);
        Assert.Empty(pipeline.OutgoingFilters);
        Assert.Empty(pipeline.MessageProcessingMiddleware);
        Assert.Empty(pipeline.SendMessageMiddleware);
    }
}
