using System.Collections.Concurrent;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

public sealed class RequestReplyManager(IMessageSerializer serializer, ISendMessagePipeline sendPipeline) : IRequestReplyManager, IReplyStatusRequestReplyManager
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
        var state = new RequestState(tcs, 1, typeof(TReply));
        _pendingRequests[messageId] = state;

        headers[HeaderKeys.RequestMessageId] = messageId.ToString();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(options.Timeout);

        await using var reg = linkedCts.Token.Register(() =>
        {
            state.Close(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                    tcs.TrySetCanceled(cancellationToken);
                else
                    tcs.TrySetException(new RequestTimeoutException(messageId, TimeSpan.FromMilliseconds(options.Timeout)));
            });
        });

        try
        {
            if (!string.IsNullOrEmpty(options.EndPoint))
                await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(TRequest), messageBytes, headers, options.EndPoint, linkedCts.Token).ConfigureAwait(false);
            else
                await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(TRequest), messageBytes, headers, null, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && linkedCts.IsCancellationRequested)
        {
        }
        catch
        {
            _pendingRequests.TryRemove(messageId, out _);
            throw;
        }

        try
        {
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
        int? configuredEndPointCount = options.EndPoints is { Count: > 0 } ? options.EndPoints.Count : null;
        // List<T> with explicit lock outperforms ConcurrentBag for the request/reply
        // fan-in case because we need Count to be O(1) and we're appending on the
        // reply thread with no parallel readers until completion.
        var responses = new List<TReply>(Math.Max(0, options.ExpectedReplyCount ?? configuredEndPointCount ?? 0));
        int expectedCount = options.ExpectedReplyCount ?? configuredEndPointCount ?? -1;
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        var state = new RequestState(tcs, expectedCount, typeof(TReply), reply =>
        {
            lock (responses)
            {
                responses.Add((TReply)reply);
            }
        });
        _pendingRequests[messageId] = state;

        headers[HeaderKeys.RequestMessageId] = messageId.ToString();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(options.Timeout);

        await using var reg = linkedCts.Token.Register(() =>
        {
            state.Close(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                    tcs.TrySetCanceled(cancellationToken);
                else
                    tcs.TrySetResult(null!); // timeout returns what we have
            });
        });

        try
        {
            if (options.EndPoints is { Count: > 0 })
            {
                foreach (string endPoint in options.EndPoints)
                    await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(TRequest), messageBytes, headers, endPoint, linkedCts.Token).ConfigureAwait(false);
            }
            else if (!string.IsNullOrEmpty(options.EndPoint))
            {
                await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(TRequest), messageBytes, headers, options.EndPoint, linkedCts.Token).ConfigureAwait(false);
            }
            else
            {
                await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(TRequest), messageBytes, headers, null, linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && linkedCts.IsCancellationRequested)
        {
        }
        catch
        {
            _pendingRequests.TryRemove(messageId, out _);
            throw;
        }

        try
        {
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

    public async Task PublishRequestAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        RequestOptions options,
        Action<TReply> onReply,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message
    {
        cancellationToken.ThrowIfCancellationRequested();

        var messageId = Guid.NewGuid();
        var expectedCount = options.ExpectedReplyCount ?? -1;
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishCompletedSuccessfully = 0;

        var state = new RequestState(tcs, expectedCount, typeof(TReply), reply => onReply((TReply)reply));
        _pendingRequests[messageId] = state;

        headers[HeaderKeys.RequestMessageId] = messageId.ToString();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(options.Timeout);

        await using var reg = linkedCts.Token.Register(() =>
        {
            state.Close(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                if (Volatile.Read(ref publishCompletedSuccessfully) == 0 && !state.HasAcceptedReplies)
                {
                    tcs.TrySetException(new RequestTimeoutException(messageId, TimeSpan.FromMilliseconds(options.Timeout)));
                    return;
                }

                // If the caller asked for a specific number of replies but the timeout
                // fired before we got them all, treat it as a timeout. Previously this
                // path succeeded silently, masking under-delivery. A zero/negative
                // expected count means "no explicit expectation" — keep success.
                if (expectedCount > 0 && !state.HasReceivedAllExpectedReplies)
                {
                    tcs.TrySetException(new RequestTimeoutException(messageId, TimeSpan.FromMilliseconds(options.Timeout)));
                    return;
                }

                tcs.TrySetResult(null!);
            });
        });

        try
        {
            await _sendPipeline.ExecutePublishMessagePipelineAsync(typeof(TRequest), messageBytes, headers, null, linkedCts.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref publishCompletedSuccessfully, 1);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && linkedCts.IsCancellationRequested)
        {
        }
        catch
        {
            _pendingRequests.TryRemove(messageId, out _);
            throw;
        }

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(messageId, out _);
        }
    }

    public void ProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type)
    {
        TryProcessReply(messageId, messageBytes, type);
    }

    public bool TryProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type)
    {
        if (!Guid.TryParse(messageId, out var requestId) || !_pendingRequests.TryGetValue(requestId, out var state))
            return false;

        lock (state.SyncRoot)
        {
            try
            {
                if (!state.TryAcceptReply(out var completesRequest))
                    return false;

                // Use the expected reply type stored at request time, not the wire-provided type.
                // This prevents deserialization into attacker-controlled types via crafted reply messages.
                object reply;
                try
                {
                    reply = _serializer.Deserialize(messageBytes, state.ReplyType);
                }
                catch (Exception ex)
                {
                    state.Close();
                    _pendingRequests.TryRemove(requestId, out _);
                    state.Tcs.TrySetException(ex);
                    return true;
                }

                if (state.OnReply != null)
                {
                    try
                    {
                        state.OnReply(reply);
                    }
                    catch (Exception ex)
                    {
                        state.Close();
                        _pendingRequests.TryRemove(requestId, out _);
                        state.Tcs.TrySetException(ex);
                        return true;
                    }

                    if (completesRequest)
                    {
                        state.Close();
                        _pendingRequests.TryRemove(requestId, out _);
                        state.Tcs.TrySetResult(null!);
                    }
                }
                else
                {
                    state.Close();
                    state.Tcs.TrySetResult(reply);
                    _pendingRequests.TryRemove(requestId, out _);
                }

                return true;
            }
            finally
            {
                state.EndReply();
            }
        }
    }

    public bool IsTrackedRequest(string messageId)
    {
        return Guid.TryParse(messageId, out var requestId) && _pendingRequests.ContainsKey(requestId);
    }

    private sealed class RequestState(TaskCompletionSource<object> tcs, int expectedCount, Type replyType, Action<object>? onReply = null)
    {
        private readonly object _syncRoot = new();
        private readonly object _stateLock = new();
        private bool _closed;
        private bool _hasAcceptedReplies;
        private Action? _pendingCloseAction;
        private int _inFlightReplies;
        private int _remainingReplies = expectedCount;

        public TaskCompletionSource<object> Tcs { get; } = tcs;
        public int ExpectedCount { get; } = expectedCount;
        public Type ReplyType { get; } = replyType;
        public Action<object>? OnReply { get; } = onReply;
        public object SyncRoot => _syncRoot;
        public bool HasAcceptedReplies
        {
            get
            {
                lock (_stateLock)
                {
                    return _hasAcceptedReplies;
                }
            }
        }

        /// <summary>
        /// True when the caller specified a positive <see cref="ExpectedCount"/> and all
        /// expected replies have been accepted. Used by PublishRequestAsync's timeout path
        /// to distinguish "got enough" from "timed out with partial replies" (3.3).
        /// </summary>
        public bool HasReceivedAllExpectedReplies
        {
            get
            {
                lock (_stateLock)
                {
                    return ExpectedCount > 0 && _remainingReplies == 0;
                }
            }
        }

        public void EndReply()
        {
            Action? closeAction = null;

            lock (_stateLock)
            {
                _inFlightReplies--;
                if (_closed && _inFlightReplies == 0 && _pendingCloseAction != null)
                {
                    closeAction = _pendingCloseAction;
                    _pendingCloseAction = null;
                }
            }

            closeAction?.Invoke();
        }

        public void Close(Action? onClose = null)
        {
            Action? closeAction = null;

            lock (_stateLock)
            {
                if (_closed)
                    return;

                _closed = true;
                if (_inFlightReplies == 0)
                    closeAction = onClose;
                else
                    _pendingCloseAction = onClose;
            }

            closeAction?.Invoke();
        }

        public bool TryAcceptReply(out bool completesRequest)
        {
            lock (_stateLock)
            {
                if (_closed)
                {
                    completesRequest = false;
                    return false;
                }

                if (ExpectedCount <= 0)
                {
                    _hasAcceptedReplies = true;
                    _inFlightReplies++;
                    completesRequest = false;
                    return true;
                }

                if (_remainingReplies <= 0)
                {
                    completesRequest = false;
                    return false;
                }

                _remainingReplies--;
                _hasAcceptedReplies = true;
                _inFlightReplies++;
                completesRequest = _remainingReplies == 0;
                return true;
            }
        }
    }
}
