using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Filters.MessageDeduplication.Filters;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Filters.MessageDeduplication;

public class IncomingDeduplicationFilterTests
{
    private readonly Mock<IMessageDeduplicationPersistor> _persistor = new();

    private IncomingDeduplicationFilter CreateFilter() => new(_persistor.Object);

    [Fact]
    public async Task ProcessAsync_NotRedelivered_Continues()
    {
        var filter = CreateFilter();
        var envelope = new Envelope { Headers = new Dictionary<string, object>() };

        var result = await filter.ProcessAsync(envelope);

        Assert.Equal(FilterAction.Continue, result);
        _persistor.Verify(p => p.GetMessageExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_RedeliveredButNotInPersistor_Continues()
    {
        var id = Guid.NewGuid();
        _persistor.Setup(p => p.GetMessageExistsAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var filter = CreateFilter();
        var envelope = new Envelope
        {
            Headers = new Dictionary<string, object>
            {
                { "Redelivered", true },
                { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) }
            }
        };

        Assert.Equal(FilterAction.Continue, await filter.ProcessAsync(envelope));
    }

    [Fact]
    public async Task ProcessAsync_RedeliveredAndInPersistor_Stops()
    {
        var id = Guid.NewGuid();
        _persistor.Setup(p => p.GetMessageExistsAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var filter = CreateFilter();
        var envelope = new Envelope
        {
            Headers = new Dictionary<string, object>
            {
                { "Redelivered", true },
                { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) }
            }
        };

        Assert.Equal(FilterAction.Stop, await filter.ProcessAsync(envelope));
    }

    [Fact]
    public async Task ProcessAsync_PersistorThrows_ExceptionPropagates()
    {
        var id = Guid.NewGuid();
        _persistor.Setup(p => p.GetMessageExistsAsync(id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var filter = CreateFilter();
        var envelope = new Envelope
        {
            Headers = new Dictionary<string, object>
            {
                { "Redelivered", true },
                { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) }
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => filter.ProcessAsync(envelope));
    }

    [Fact]
    public async Task ProcessAsync_PreCancelledToken_ThrowsOCE()
    {
        var id = Guid.NewGuid();
        _persistor.Setup(p => p.GetMessageExistsAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var filter = CreateFilter();
        var envelope = new Envelope
        {
            Headers = new Dictionary<string, object>
            {
                { "Redelivered", true },
                { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) }
            }
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            filter.ProcessAsync(envelope, cts.Token));
    }
}
