using System.Collections.Concurrent;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

public sealed class RequestReplyManager(IMessageSerializer serializer, ISendMessagePipeline sendPipeline) : IRequestReplyManager
{
    private readonly ConcurrentDictionary<Guid, RequestState> _pendingRequests = new();
    private readonly IMessageSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    private readonly ISendMessagePipeline _sendPipeline = sendPipeline ?? throw new ArgumentNullException(nameof(sendPipeline));

    public async Task<TReply> SendRequestAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message
    {
        cancellationToken.ThrowIfCancellationRequested();

        var messageId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[messageId] = new RequestState(tcs, 1, typeof(TReply));

        headers[HeaderKeys.RequestMessageId] = messageId.ToString();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(options.Timeout);

        await using var reg = linkedCts.Token.Register(() =>
        {
            // Remove atomically before signalling so ProcessReply cannot add to the
            // entry after the timeout/cancel fires (closes TOCTOU race R-021).
            _pendingRequests.TryRemove(messageId, out _);
            if (cancellationToken.IsCancellationRequested)
                tcs.TrySetCanceled(cancellationToken);
            else
                tcs.TrySetException(new RequestTimeoutException(messageId, TimeSpan.FromMilliseconds(options.Timeout)));
        });

        try
        {
            if (!string.IsNullOrEmpty(options.EndPoint))
                await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(TRequest), messageBytes, headers, options.EndPoint, cancellationToken).ConfigureAwait(false);
            else
                await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(TRequest), messageBytes, headers, null, cancellationToken).ConfigureAwait(false);

            var result = await tcs.Task.ConfigureAwait(false);
            return (TReply)result;
        }
        finally
        {
            _pendingRequests.TryRemove(messageId, out _);
        }
    }

    public async Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message
    {
        cancellationToken.ThrowIfCancellationRequested();

        var messageId = Guid.NewGuid();
        // List<T> with explicit lock outperforms ConcurrentBag for the request/reply
        // fan-in case because we need Count to be O(1) and we're appending on the
        // reply thread with no parallel readers until completion (P-18).
        var responses = new List<TReply>(Math.Max(0, options.ExpectedReplyCount ?? options.EndPoints?.Count ?? 0));
        int expectedCount = options.ExpectedReplyCount ?? options.EndPoints?.Count ?? -1;
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingRequests[messageId] = new RequestState(tcs, expectedCount, typeof(TReply), reply =>
        {
            lock (responses)
            {
                responses.Add((TReply)reply);
                if (expectedCount > 0 && responses.Count >= expectedCount)
                    tcs.TrySetResult(null!);
            }
        });

        headers[HeaderKeys.RequestMessageId] = messageId.ToString();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(options.Timeout);

        await using var reg = linkedCts.Token.Register(() =>
        {
            // Remove atomically before signalling so ProcessReply cannot append to the
            // response list after the timeout/cancel fires (closes TOCTOU race R-021).
            _pendingRequests.TryRemove(messageId, out _);
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
                    await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(TRequest), messageBytes, headers, endPoint, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(TRequest), messageBytes, headers, null, cancellationToken).ConfigureAwait(false);
            }

            await tcs.Task.ConfigureAwait(false);
            lock (responses)
            {
                return [.. responses];
            }
        }
        finally
        {
            _pendingRequests.TryRemove(messageId, out _);
        }
    }

    public void ProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type)
    {
        if (!Guid.TryParse(messageId, out var requestId) || !_pendingRequests.TryGetValue(requestId, out var state))
            return;

        // Use the expected reply type stored at request time, not the wire-provided type.
        // This prevents deserialization into attacker-controlled types via crafted reply messages.
        object reply = _serializer.Deserialize(messageBytes.ToArray(), state.ReplyType);

        if (state.OnReply != null)
        {
            state.OnReply(reply);
        }
        else
        {
            state.Tcs.TrySetResult(reply);
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private record RequestState(
        TaskCompletionSource<object> Tcs,
        int ExpectedCount,
        Type ReplyType,
        Action<object>? OnReply = null);
}
