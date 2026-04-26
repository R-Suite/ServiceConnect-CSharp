using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using Xunit;

namespace ServiceConnect.Filters.MessageDeduplication.Tests;

public class MessageDeduplicationPersistorInMemoryTests
{
    [Fact]
    public async Task InsertAsync_ThenGetMessageExistsAsync_ReturnsTrue()
    {
        var persistor = new MessageDeduplicationPersistorInMemory();
        var id = Guid.NewGuid();

        await persistor.InsertAsync(id, DateTime.UtcNow.AddHours(1));
        var exists = await persistor.GetMessageExistsAsync(id);

        Assert.True(exists);
    }

    [Fact]
    public async Task GetMessageExistsAsync_UnknownId_ReturnsFalse()
    {
        var persistor = new MessageDeduplicationPersistorInMemory();
        var exists = await persistor.GetMessageExistsAsync(Guid.NewGuid());
        Assert.False(exists);
    }

    [Fact]
    public async Task RemoveExpiredMessagesAsync_RemovesOnlyExpired()
    {
        var persistor = new MessageDeduplicationPersistorInMemory();
        var expired = Guid.NewGuid();
        var fresh = Guid.NewGuid();

        await persistor.InsertAsync(expired, DateTime.UtcNow.AddHours(-1)); // already expired
        await persistor.InsertAsync(fresh, DateTime.UtcNow.AddHours(1));

        await persistor.RemoveExpiredMessagesAsync(DateTime.UtcNow);

        Assert.False(await persistor.GetMessageExistsAsync(expired));
        Assert.True(await persistor.GetMessageExistsAsync(fresh));
    }

    [Fact]
    public async Task InsertAsync_PreCancelledToken_ThrowsOCE()
    {
        var persistor = new MessageDeduplicationPersistorInMemory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            persistor.InsertAsync(Guid.NewGuid(), DateTime.UtcNow.AddHours(1), cts.Token));
    }

    [Fact]
    public async Task GetMessageExistsAsync_PreCancelledToken_ThrowsOCE()
    {
        var persistor = new MessageDeduplicationPersistorInMemory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            persistor.GetMessageExistsAsync(Guid.NewGuid(), cts.Token));
    }

    [Fact]
    public async Task RemoveExpiredMessagesAsync_PreCancelledToken_ThrowsOCE()
    {
        var persistor = new MessageDeduplicationPersistorInMemory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            persistor.RemoveExpiredMessagesAsync(DateTime.UtcNow, cts.Token));
    }
}
