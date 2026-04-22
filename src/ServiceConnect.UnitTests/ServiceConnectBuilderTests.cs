using System.Reflection;
using ServiceConnect;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

public class TestFilter : IFilter
{
    public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default) => Task.FromResult(true);
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
    public void ScanAssemblies_NullArray_Throws()
    {
        var builder = new ServiceConnectBuilder();

        Assert.Throws<ArgumentNullException>(() => builder.ScanAssemblies((Assembly[])null!));
    }

    [Fact]
    public void ScanAssemblies_NullElement_Throws()
    {
        // A null assembly element must be caught at the boundary (with the
        // offending index named) rather than surfacing later as an NRE inside
        // HandlerScanner, where the failure mode is obscure.
        var builder = new ServiceConnectBuilder();

        var ex = Assert.Throws<ArgumentNullException>(
            () => builder.ScanAssemblies(typeof(TestFilter).Assembly, null!));

        Assert.Contains("assemblies[1]", ex.Message);
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
