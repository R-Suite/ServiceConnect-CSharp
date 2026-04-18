namespace ServiceConnect.EndToEndTests.Helpers;

internal static class TestPolling
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);

    public static async Task<T?> WaitForAsync<T>(
        Func<Task<T?>> probe,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var interval = pollInterval ?? DefaultPollInterval;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await probe().ConfigureAwait(false);
            if (result is not null) return result;
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    public static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? DefaultPollInterval;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await condition().ConfigureAwait(false)) return true;
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }
}
