using System.Collections.Concurrent;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

public sealed class RequestReplyManager(IMessageSerializer serializer) : IRequestReplyManager
{
    private readonly ConcurrentDictionary<string, RequestState> _pendingRequests = new();
    private readonly IMessageSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

    public async Task<TReply> SendRequestAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        Func<Type, byte[], Dictionary<string, string>, string?, CancellationToken, Task> sendAction,
        RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message
    {
        cancellationToken.ThrowIfCancellationRequested();

        var messageId = Guid.NewGuid();
        var messageIdStr = messageId.ToString();
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[messageIdStr] = new RequestState(tcs, 1, typeof(TReply));

        headers[HeaderKeys.RequestMessageId] = messageIdStr;

        using var timeoutCts = new CancellationTokenSource(options.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        await using var reg = linkedCts.Token.Register(() =>
        {
            if (cancellationToken.IsCancellationRequested)
                tcs.TrySetCanceled(cancellationToken);
            else
                tcs.TrySetException(new RequestTimeoutException(messageId, TimeSpan.FromMilliseconds(options.Timeout)));
        });

        try
        {
            if (!string.IsNullOrEmpty(options.EndPoint))
                await sendAction(typeof(TRequest), messageBytes, headers, options.EndPoint, cancellationToken).ConfigureAwait(false);
            else
                await sendAction(typeof(TRequest), messageBytes, headers, null, cancellationToken).ConfigureAwait(false);

            var result = await tcs.Task.ConfigureAwait(false);
            return (TReply)result;
        }
        finally
        {
            _pendingRequests.TryRemove(messageIdStr, out _);
        }
    }

    public async Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        Func<Type, byte[], Dictionary<string, string>, string?, CancellationToken, Task> sendAction,
        RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message
    {
        cancellationToken.ThrowIfCancellationRequested();

        var messageId = Guid.NewGuid();
        var messageIdStr = messageId.ToString();
        var responses = new ConcurrentBag<TReply>();
        int expectedCount = options.ExpectedReplyCount ?? options.EndPoints?.Count ?? -1;
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingRequests[messageIdStr] = new RequestState(tcs, expectedCount, typeof(TReply), reply =>
        {
            responses.Add((TReply)reply);
            if (expectedCount > 0 && responses.Count >= expectedCount)
                tcs.TrySetResult(null!);
        });

        headers[HeaderKeys.RequestMessageId] = messageIdStr;

        using var timeoutCts = new CancellationTokenSource(options.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        await using var reg = linkedCts.Token.Register(() =>
        {
            if (cancellationToken.IsCancellationRequested)
                tcs.TrySetCanceled(cancellationToken);
            else
                tcs.TrySetResult(null!); // timeout returns what we have
        });

        try
        {
            if (options.EndPoints != null)
            {
                foreach (string endPoint in options.EndPoints)
                    await sendAction(typeof(TRequest), messageBytes, headers, endPoint, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await sendAction(typeof(TRequest), messageBytes, headers, null, cancellationToken).ConfigureAwait(false);
            }

            await tcs.Task.ConfigureAwait(false);
            return [.. responses];
        }
        finally
        {
            _pendingRequests.TryRemove(messageIdStr, out _);
        }
    }

    public void ProcessReply(string messageId, byte[] messageBytes, Type type)
    {
        if (!_pendingRequests.TryGetValue(messageId, out var state))
            return;

        // Use the expected reply type stored at request time, not the wire-provided type.
        // This prevents deserialization into attacker-controlled types via crafted reply messages.
        object reply = _serializer.Deserialize(messageBytes, state.ReplyType);

        if (state.OnReply != null)
        {
            state.OnReply(reply);
        }
        else
        {
            state.Tcs.TrySetResult(reply);
            _pendingRequests.TryRemove(messageId, out _);
        }
    }

    private record RequestState(
        TaskCompletionSource<object> Tcs,
        int ExpectedCount,
        Type ReplyType,
        Action<object>? OnReply = null);
}
