using ServiceConnect;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

public class TestFilter : IFilter
{
    public IBus Bus { get; set; } = null!;
    public bool Process(Envelope envelope) => true;
}

public class ServiceConnectBuilderTests
{
    [Fact]
    public void ConfigureTransport_SetsTransportProperties()
    {
        var builder = new ServiceConnectBuilder();

        builder.ConfigureTransport(t => t.Host = "myhost");

        Assert.Equal("myhost", builder.BusConfig.Transport.Host);
    }

    [Fact]
    public void ConfigureQueues_SetsQueueProperties()
    {
        var builder = new ServiceConnectBuilder();

        builder.ConfigureQueues(q => q.QueueName = "test-queue");

        Assert.Equal("test-queue", builder.BusConfig.Queues.QueueName);
    }

    [Fact]
    public void ConfigurePipeline_SetsPipelineProperties()
    {
        var builder = new ServiceConnectBuilder();

        builder.ConfigurePipeline(p => p.OutgoingFilters.Add(typeof(TestFilter)));

        Assert.Single(builder.BusConfig.Pipeline.OutgoingFilters);
    }

    [Fact]
    public void AddOutgoingFilter_AddsToOutgoingFilters()
    {
        var builder = new ServiceConnectBuilder();

        builder.AddOutgoingFilter<TestFilter>();

        Assert.Contains(typeof(TestFilter), builder.BusConfig.Pipeline.OutgoingFilters);
    }

    [Fact]
    public void AddBeforeConsumingFilter_AddsToBeforeConsumingFilters()
    {
        var builder = new ServiceConnectBuilder();

        builder.AddBeforeConsumingFilter<TestFilter>();

        Assert.Contains(typeof(TestFilter), builder.BusConfig.Pipeline.BeforeConsumingFilters);
    }

    [Fact]
    public void AddAfterConsumingFilter_AddsToAfterConsumingFilters()
    {
        var builder = new ServiceConnectBuilder();

        builder.AddAfterConsumingFilter<TestFilter>();

        Assert.Contains(typeof(TestFilter), builder.BusConfig.Pipeline.AfterConsumingFilters);
    }

    [Fact]
    public void FluentChaining_ReturnsBuilderInstance()
    {
        var builder = new ServiceConnectBuilder();

        var result = builder
            .ConfigureTransport(t => t.Host = "myhost")
            .ConfigureQueues(q => q.QueueName = "test-queue")
            .AddOutgoingFilter<TestFilter>()
            .AddBeforeConsumingFilter<TestFilter>()
            .AddAfterConsumingFilter<TestFilter>();

        Assert.Same(builder, result);
    }
}
