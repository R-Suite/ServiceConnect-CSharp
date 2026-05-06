using System.Collections.Concurrent;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

/// <summary>
/// Tracks pending request-reply exchanges and correlates incoming replies with the originating request.
/// </summary>
public sealed class RequestReplyManager(IMessageSerializer serializer, ISendMessagePipeline sendPipeline) : IRequestReplyManager, IReplyStatusRequestReplyManager
{
    private readonly ConcurrentDictionary<Guid, RequestState> _pendingRequests = new();
    private readonly IMessageSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    private readonly ISendMessagePipeline _sendPipeline = sendPipeline ?? throw new ArgumentNullException(nameof(sendPipeline));

    /// <inheritdoc />
    /// <remarks>
    /// The internal cancellation registration is asynchronously disposed when the request
    /// completes (success, timeout, or cancellation), matching the <c>await using</c> pattern
    /// used in this implementation.
    /// </remarks>
    public async Task<TReply> SendRequestAsync<TRequest, TReply>(
        TRequest message,
        IDictionary<string, string> headers,
        RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message
    {
        ValidateOptions(options);

        cancellationToken.ThrowIfCancellationRequested();

        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        _serializer.Serialize(message, bufferWriter);
        var messageBytes = bufferWriter.WrittenMemory;

        var messageId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new RequestState(tcs, 1, typeof(TReply));
        _pendingRequests[messageId] = state;

        headers[HeaderKeys.RequestMessageId] = messageId.ToString();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(options.Timeout);

        // Tracks whether the outbound pipeline finished its work before the linked CTS
        // fired. Used by the typed-cancel catch below to distinguish "send pipeline was
        // cancelled mid-flight" (fail fast) from "send completed and reply never arrived"
        // (let the timeout path surface RequestTimeoutException).
        var sendCompleted = 0;

        await using var reg = linkedCts.Token.Register(() =>
        {
            state.Close(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
                else
                {
                    tcs.TrySetException(new RequestTimeoutException(messageId, TimeSpan.FromMilliseconds(options.Timeout)));
                }
            });
        }).ConfigureAwait(false);

        try
        {
            var endPoint = string.IsNullOrEmpty(options.EndPoint) ? null : options.EndPoint;
            var context = new SendContext
            {
                Message = message,
                MessageType = typeof(TRequest),
                MessageBytes = messageBytes,
                Headers = headers,
                EndPoint = endPoint,
                RoutingKey = null,
                Operation = SendOperation.Request,
            };
            await _sendPipeline.ExecuteSendMessagePipelineAsync(context, linkedCts.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref sendCompleted, 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller's own token fired. Drop the pending entry and let the bare OCE
            // propagate so existing handlers continue to observe a vanilla cancellation.
            _pendingRequests.TryRemove(messageId, out _);
            throw;
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && Volatile.Read(ref sendCompleted) == 0)
        {
            // The linked CTS fired (timeout) BEFORE the send pipeline finished and the
            // caller's token did not. The reply will never arrive, so fail fast with the
            // typed exception instead of waiting on the TCS until the timeout deadline.
            _pendingRequests.TryRemove(messageId, out _);
            // The registration callback may have already (or will momentarily) fault the TCS
            // with RequestTimeoutException. Since we're throwing the typed cancel exception
            // now and never awaiting tcs.Task, attach a fault observer to prevent the
            // unawaited faulted task from triggering TaskScheduler.UnobservedTaskException
            // at finalization.
            _ = tcs.Task.ContinueWith(static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new RequestSendCancelledException(messageId,
                $"Request {messageId} send pipeline was cancelled before delivery.",
                linkedCts.Token);
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

    /// <inheritdoc />
    /// <remarks>
    /// The internal cancellation registration is asynchronously disposed when the request
    /// completes (success, timeout, or cancellation), matching the <c>await using</c> pattern
    /// used in this implementation.
    /// </remarks>
    public async Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(
        TRequest message,
        IDictionary<string, string> headers,
        RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message
    {
        ValidateOptions(options);

        cancellationToken.ThrowIfCancellationRequested();

        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        _serializer.Serialize(message, bufferWriter);
        var messageBytes = bufferWriter.WrittenMemory;

        var messageId = Guid.NewGuid();
        // List<T> with explicit lock outperforms ConcurrentBag for the request/reply
        // fan-in case because we need Count to be O(1) and we're appending on the
        // reply thread with no parallel readers until completion.
        var responses = new List<TReply>(Math.Max(0, options.ExpectedReplyCount ?? 0));
        int expectedCount = options.ExpectedReplyCount ?? -1;
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

        // See SendRequestAsync for the rationale; sendCompleted is flipped only after
        // the send completes so a cancellation during the send also fails fast.
        var sendCompleted = 0;

        await using var reg = linkedCts.Token.Register(() =>
        {
            state.Close(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
                else
                {
                    tcs.TrySetResult(null!); // timeout returns what we have
                }
            });
        }).ConfigureAwait(false);

        try
        {
            var endPoint = string.IsNullOrEmpty(options.EndPoint) ? null : options.EndPoint;
            var context = new SendContext
            {
                Message = message,
                MessageType = typeof(TRequest),
                MessageBytes = messageBytes,
                Headers = headers,
                EndPoint = endPoint,
                RoutingKey = null,
                Operation = SendOperation.Request,
            };
            await _sendPipeline.ExecuteSendMessagePipelineAsync(context, linkedCts.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref sendCompleted, 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _pendingRequests.TryRemove(messageId, out _);
            throw;
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && Volatile.Read(ref sendCompleted) == 0)
        {
            // Timeout cancelled the send before it finished. Surface the typed exception
            // immediately rather than returning the partial-results path, which would
            // otherwise hand the caller an empty list and obscure the transport-layer failure.
            _pendingRequests.TryRemove(messageId, out _);
            // The registration callback may have already (or will momentarily) fault the TCS
            // with RequestTimeoutException. Since we're throwing the typed cancel exception
            // now and never awaiting tcs.Task, attach a fault observer to prevent the
            // unawaited faulted task from triggering TaskScheduler.UnobservedTaskException
            // at finalization.
            _ = tcs.Task.ContinueWith(static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new RequestSendCancelledException(messageId,
                $"Request {messageId} send pipeline was cancelled before delivery.",
                linkedCts.Token);
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

    /// <inheritdoc />
    /// <remarks>
    /// The internal cancellation registration is asynchronously disposed when the request
    /// completes (success, timeout, or cancellation), matching the <c>await using</c> pattern
    /// used in this implementation.
    /// </remarks>
    public async Task PublishRequestAsync<TRequest, TReply>(
        TRequest message,
        IDictionary<string, string> headers,
        RequestOptions options,
        Action<TReply> onReply,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message
    {
        ValidateOptions(options);

        cancellationToken.ThrowIfCancellationRequested();

        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        _serializer.Serialize(message, bufferWriter);
        var messageBytes = bufferWriter.WrittenMemory;

        var messageId = Guid.NewGuid();
        var expectedCount = options.ExpectedReplyCount ?? -1;
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCompleted = 0;

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

                if (Volatile.Read(ref sendCompleted) == 0 && !state.HasAcceptedReplies)
                {
                    tcs.TrySetException(new RequestTimeoutException(messageId, TimeSpan.FromMilliseconds(options.Timeout)));
                    return;
                }

                // If the caller asked for a specific number of replies but the timeout
                // fired before we got them all, surface a RequestTimeoutException so
                // under-delivery is visible to the caller. A zero/negative expected
                // count means "no explicit expectation" — keep success in that case.
                if (expectedCount > 0 && !state.HasReceivedAllExpectedReplies)
                {
                    tcs.TrySetException(new RequestTimeoutException(messageId, TimeSpan.FromMilliseconds(options.Timeout)));
                    return;
                }

                tcs.TrySetResult(null!);
            });
        }).ConfigureAwait(false);

        try
        {
            var context = new SendContext
            {
                Message = message,
                MessageType = typeof(TRequest),
                MessageBytes = messageBytes,
                Headers = headers,
                EndPoint = null,
                RoutingKey = null,
                Operation = SendOperation.Request,
            };
            await _sendPipeline.ExecutePublishMessagePipelineAsync(context, linkedCts.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref sendCompleted, 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _pendingRequests.TryRemove(messageId, out _);
            throw;
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && Volatile.Read(ref sendCompleted) == 0)
        {
            // Publish pipeline was cancelled by the timeout before delivery; surface the
            // typed exception so the caller sees a fail-fast outcome instead of the
            // timeout-shaped completion of the reply TCS.
            _pendingRequests.TryRemove(messageId, out _);
            // The registration callback may have already (or will momentarily) fault the TCS
            // with RequestTimeoutException. Since we're throwing the typed cancel exception
            // now and never awaiting tcs.Task, attach a fault observer to prevent the
            // unawaited faulted task from triggering TaskScheduler.UnobservedTaskException
            // at finalization.
            _ = tcs.Task.ContinueWith(static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new RequestSendCancelledException(messageId,
                $"Publish {messageId} send pipeline was cancelled before delivery.",
                linkedCts.Token);
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

    /// <summary>
    /// Processes a reply for a previously tracked request and ignores unknown request identifiers.
    /// </summary>
    /// <param name="messageId">The request identifier copied into the reply message.</param>
    /// <param name="messageBytes">The serialized reply payload.</param>
    /// <param name="type">The wire-reported reply type.</param>
    public void ProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type)
    {
        TryProcessReply(messageId, messageBytes, type);
    }

    /// <summary>
    /// Attempts to apply a reply message to a tracked request.
    /// </summary>
    /// <param name="messageId">The request identifier copied into the reply message.</param>
    /// <param name="messageBytes">The serialized reply payload.</param>
    /// <param name="type">The wire-reported reply type.</param>
    /// <returns><see langword="true"/> when the reply matched a tracked request; otherwise <see langword="false"/>.</returns>
    public bool TryProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type)
    {
        if (!Guid.TryParse(messageId, out var requestId) || !_pendingRequests.TryGetValue(requestId, out var state))
        {
            return false;
        }

        // The whole reply lifecycle (deserialize, OnReply callback, completion bookkeeping)
        // runs under RequestState._stateLock so concurrent replies cannot re-enter the
        // user callback. Use the expected reply type stored at request time, not the
        // wire-provided type — this prevents deserialization into attacker-controlled
        // types via crafted reply messages.
        if (!state.TryHandleReply(
            replyType => _serializer.Deserialize(messageBytes, replyType),
            out var requestCompleted,
            out var completionWork))
        {
            return false;
        }

        if (requestCompleted)
        {
            _pendingRequests.TryRemove(requestId, out _);
        }

        // TaskCompletionSource continuations may run inline on the calling thread; running
        // them outside the state lock keeps a slow continuation from blocking another
        // request's reply path that lands on the same state.
        completionWork?.Invoke();
        return true;
    }

    /// <inheritdoc />
    public bool IsTrackedRequest(string messageId)
    {
        return Guid.TryParse(messageId, out var requestId) && _pendingRequests.ContainsKey(requestId);
    }

    private static void ValidateOptions(RequestOptions options)
    {
        if (options.Timeout is < 0 and not Timeout.Infinite)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(RequestOptions)}.{nameof(RequestOptions.Timeout)} must be non-negative or Timeout.Infinite.");
        }

        // default(RequestOptions) skips the parameterless ctor and leaves Timeout=0,
        // which would CancelAfter(0) and immediately fail every caller. Reject with
        // pointer to the right replacement.
        if (options.Timeout == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(RequestOptions)}.{nameof(RequestOptions.Timeout)} is 0 (likely default(RequestOptions)). " +
                $"Use RequestOptions.Default or new RequestOptions() to get the default {RequestOptions.DefaultTimeoutMs}ms timeout, " +
                $"or set Timeout = Timeout.Infinite to wait indefinitely.");
        }
    }

    private sealed class RequestState(TaskCompletionSource<object> tcs, int expectedCount, Type replyType, Action<object>? onReply = null)
    {
#if NET9_0_OR_GREATER
        private readonly System.Threading.Lock _stateLock = new();
#else
        private readonly object _stateLock = new();
#endif
        private bool _closed;
        private bool _hasAcceptedReplies;
        private int _remainingReplies = expectedCount;

        public TaskCompletionSource<object> Tcs { get; } = tcs;
        public int ExpectedCount { get; } = expectedCount;
        public Type ReplyType { get; } = replyType;
        public Action<object>? OnReply { get; } = onReply;
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
        /// to distinguish "got enough" from "timed out with partial replies".
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

        /// <summary>
        /// Close runs the supplied close action exactly once (the first caller wins).
        /// The action runs outside the lock so a slow continuation cannot pin the
        /// dispatch thread that observed the close.
        /// </summary>
        internal void Close(Action? onClose = null)
        {
            lock (_stateLock)
            {
                if (_closed)
                {
                    return;
                }
                _closed = true;
            }
            onClose?.Invoke();
        }

        /// <summary>
        /// Processes a deserialized reply under the state lock. Returns <see langword="true"/>
        /// when the reply was accepted (state was open and the reply-count budget allowed)
        /// or <see langword="false"/> when rejected (state already closed or budget exhausted).
        /// The user-supplied <see cref="OnReply"/> callback runs under the state lock so
        /// concurrent replies cannot re-enter it.
        /// <paramref name="completionWork"/> contains TCS continuations (TrySetResult / TrySetException);
        /// the caller invokes it outside the lock so a slow continuation does not pin the
        /// reply dispatch thread.
        /// </summary>
        internal bool TryHandleReply(
            Func<Type, object> deserialize,
            out bool requestCompleted,
            out Action? completionWork)
        {
            completionWork = null;
            requestCompleted = false;

            lock (_stateLock)
            {
                if (_closed)
                {
                    return false;
                }

                bool acceptedAndCompletes;
                if (ExpectedCount <= 0)
                {
                    _hasAcceptedReplies = true;
                    acceptedAndCompletes = false;
                }
                else
                {
                    if (_remainingReplies <= 0)
                    {
                        return false;
                    }
                    _remainingReplies--;
                    _hasAcceptedReplies = true;
                    acceptedAndCompletes = _remainingReplies == 0;
                }

                // Deserialize inside the lock so a corrupted-payload exception attributes
                // to this reply without leaking partial state mutations to a concurrent reply.
                object reply;
                try
                {
                    reply = deserialize(ReplyType);
                }
                catch (Exception ex)
                {
                    _closed = true;
                    requestCompleted = true;
                    completionWork = () => Tcs.TrySetException(ex);
                    return true;
                }

                if (OnReply is not null)
                {
                    try
                    {
                        OnReply(reply);
                    }
                    catch (Exception ex)
                    {
                        _closed = true;
                        requestCompleted = true;
                        completionWork = () => Tcs.TrySetException(ex);
                        return true;
                    }

                    if (acceptedAndCompletes)
                    {
                        _closed = true;
                        requestCompleted = true;
                        completionWork = () => Tcs.TrySetResult(null!);
                    }
                }
                else
                {
                    _closed = true;
                    requestCompleted = true;
                    completionWork = () => Tcs.TrySetResult(reply);
                }

                return true;
            }
        }
    }
}
