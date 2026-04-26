using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public abstract class FakeFilter1 : IFilter
{
    public abstract Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default);
}

public abstract class FakeFilter2 : IFilter
{
    public abstract Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default);
}

public class FilterPipelineTests
{
    private readonly PipelineConfiguration _config;
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly FilterPipeline _pipeline;

    public FilterPipelineTests()
    {
        _config = new PipelineConfiguration();
        _mockServiceProvider = new Mock<IServiceProvider>();
        // ConsumeScopeAccessor flows the scope through AsyncLocal — each xUnit
        // test instance runs in its own async flow, so pushing in the ctor and
        // discarding the disposable is safe.
        var scopeAccessor = new ConsumeScopeAccessor();
        scopeAccessor.Push(_mockServiceProvider.Object);
        _pipeline = new FilterPipeline(_config, scopeAccessor);
    }

    [Fact]
    public async Task ExecuteOutgoingFiltersAsync_WithNoFilters_ReturnsContinue()
    {
        var envelope = new Envelope();
        var result = await _pipeline.ExecuteOutgoingFiltersAsync(envelope);
        Assert.Equal(FilterAction.Continue, result);
    }

    [Fact]
    public async Task ExecuteOutgoingFiltersAsync_WhenFilterContinues_PipelineContinues()
    {
        var mockFilter = new Mock<FakeFilter1>();
        mockFilter.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Continue);

        _mockServiceProvider
            .Setup(sp => sp.GetService(typeof(FakeFilter1)))
            .Returns(mockFilter.Object);

        _config.OutgoingFilters.Add(typeof(FakeFilter1));

        var envelope = new Envelope();
        var result = await _pipeline.ExecuteOutgoingFiltersAsync(envelope);

        Assert.Equal(FilterAction.Continue, result);
    }

    [Fact]
    public async Task ExecuteOutgoingFiltersAsync_WhenFilterStops_PipelineStops()
    {
        var mockFilter = new Mock<FakeFilter1>();
        mockFilter.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Stop);

        _mockServiceProvider
            .Setup(sp => sp.GetService(typeof(FakeFilter1)))
            .Returns(mockFilter.Object);

        _config.OutgoingFilters.Add(typeof(FakeFilter1));

        var envelope = new Envelope();
        var result = await _pipeline.ExecuteOutgoingFiltersAsync(envelope);

        Assert.Equal(FilterAction.Stop, result);
    }

    [Fact]
    public async Task ExecuteOutgoingFiltersAsync_ExecutesFiltersInOrder()
    {
        var callOrder = new List<string>();

        var mockFilter1 = new Mock<FakeFilter1>();
        mockFilter1.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("filter1"))
            .ReturnsAsync(FilterAction.Continue);

        var mockFilter2 = new Mock<FakeFilter2>();
        mockFilter2.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("filter2"))
            .ReturnsAsync(FilterAction.Continue);

        _mockServiceProvider
            .Setup(sp => sp.GetService(typeof(FakeFilter1)))
            .Returns(mockFilter1.Object);
        _mockServiceProvider
            .Setup(sp => sp.GetService(typeof(FakeFilter2)))
            .Returns(mockFilter2.Object);

        _config.OutgoingFilters.Add(typeof(FakeFilter1));
        _config.OutgoingFilters.Add(typeof(FakeFilter2));

        var envelope = new Envelope();
        await _pipeline.ExecuteOutgoingFiltersAsync(envelope);

        Assert.Equal(new[] { "filter1", "filter2" }, callOrder);
    }

    [Fact]
    public async Task ExecuteOutgoingFiltersAsync_StopsAtFirstStoppingFilter()
    {
        var mockFilter1 = new Mock<FakeFilter1>();
        mockFilter1.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Stop);

        var mockFilter2 = new Mock<FakeFilter2>();
        mockFilter2.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Continue);

        _mockServiceProvider
            .Setup(sp => sp.GetService(typeof(FakeFilter1)))
            .Returns(mockFilter1.Object);
        _mockServiceProvider
            .Setup(sp => sp.GetService(typeof(FakeFilter2)))
            .Returns(mockFilter2.Object);

        _config.OutgoingFilters.Add(typeof(FakeFilter1));
        _config.OutgoingFilters.Add(typeof(FakeFilter2));

        var envelope = new Envelope();
        await _pipeline.ExecuteOutgoingFiltersAsync(envelope);

        // Filter2 should never have been called once filter1 said Stop.
        mockFilter2.Verify(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteOutgoingFiltersAsync_ThrowsWhenFilterNotRegistered()
    {
        _mockServiceProvider
            .Setup(sp => sp.GetService(typeof(FakeFilter1)))
            .Returns(null!);

        _config.OutgoingFilters.Add(typeof(FakeFilter1));

        var envelope = new Envelope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _pipeline.ExecuteOutgoingFiltersAsync(envelope));
    }

    [Fact]
    public async Task ExecuteBeforeConsumingFiltersAsync_WithNoFilters_ReturnsContinue()
    {
        var envelope = new Envelope();
        var result = await _pipeline.ExecuteBeforeConsumingFiltersAsync(envelope);
        Assert.Equal(FilterAction.Continue, result);
    }

    [Fact]
    public async Task ExecuteAfterConsumingFiltersAsync_WithNoFilters_ReturnsContinue()
    {
        var envelope = new Envelope();
        var result = await _pipeline.ExecuteAfterConsumingFiltersAsync(envelope);
        Assert.Equal(FilterAction.Continue, result);
    }

    [Fact]
    public async Task ExecuteFilter_ResolvesFromCurrentScope()
    {
        // Filters must be resolved from the scope pushed onto ConsumeScopeAccessor,
        // not from any previously captured provider. Swap the current scope mid-flight
        // and verify the new provider is the one queried.
        var firstFilter = new Mock<FakeFilter1>();
        firstFilter.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Continue);
        _mockServiceProvider.Setup(sp => sp.GetService(typeof(FakeFilter1))).Returns(firstFilter.Object);

        var otherProvider = new Mock<IServiceProvider>();
        var swappedFilter = new Mock<FakeFilter1>();
        swappedFilter.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Continue);
        otherProvider.Setup(sp => sp.GetService(typeof(FakeFilter1))).Returns(swappedFilter.Object);

        _config.OutgoingFilters.Add(typeof(FakeFilter1));

        var accessor = new ConsumeScopeAccessor();
        accessor.Push(otherProvider.Object);
        var pipeline = new FilterPipeline(_config, accessor);

        await pipeline.ExecuteOutgoingFiltersAsync(new Envelope());

        swappedFilter.Verify(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Once);
        firstFilter.Verify(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteFilter_ThrowsWhenNoScopePushed()
    {
        // Guard-rail: resolving a filter with no scope pushed is always a misuse.
        // Throw loudly rather than silently falling back to a root provider.
        var config = new PipelineConfiguration();
        config.OutgoingFilters.Add(typeof(FakeFilter1));
        var pipeline = new FilterPipeline(config, new ConsumeScopeAccessor());

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteOutgoingFiltersAsync(new Envelope()));
    }

    [Fact]
    public async Task ExecuteOutgoingFiltersAsync_PreCancelledToken_ThrowsOCEBeforeFilterRuns()
    {
        var mockFilter = new Mock<FakeFilter1>();
        mockFilter.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Continue);

        _mockServiceProvider
            .Setup(sp => sp.GetService(typeof(FakeFilter1)))
            .Returns(mockFilter.Object);

        _config.OutgoingFilters.Add(typeof(FakeFilter1));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var envelope = new Envelope();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _pipeline.ExecuteOutgoingFiltersAsync(envelope, cts.Token));

        mockFilter.Verify(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
