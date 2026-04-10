using System.Collections.Concurrent;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Services;

public class RequestReplyManager : IRequestReplyManager
{
    private readonly ConcurrentDictionary<string, RequestState> _pendingRequests = new();

    public async Task<TReply> SendRequestAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        Func<Type, byte[], Dictionary<string, string>, string?, Task> sendAction,
        RequestOptions options)
        where TRequest : Message
        where TReply : Message
    {
        var messageId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<object>();
        _pendingRequests[messageId.ToString()] = new RequestState(tcs, 1);

        headers["RequestMessageId"] = messageId.ToString();

        if (!string.IsNullOrEmpty(options.EndPoint))
            await sendAction(typeof(TRequest), messageBytes, headers, options.EndPoint);
        else
            await sendAction(typeof(TRequest), messageBytes, headers, null);

        using var cts = new CancellationTokenSource(options.Timeout);
        cts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            var result = await tcs.Task;
            return (TReply)result;
        }
        catch (OperationCanceledException)
        {
            _pendingRequests.TryRemove(messageId.ToString(), out _);
            throw new RequestTimeoutException(messageId, TimeSpan.FromMilliseconds(options.Timeout));
        }
    }

    public async Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        Func<Type, byte[], Dictionary<string, string>, string?, Task> sendAction,
        RequestOptions options)
        where TRequest : Message
        where TReply : Message
    {
        var messageId = Guid.NewGuid();
        var responses = new List<TReply>();
        int expectedCount = options.ExpectedReplyCount ?? options.EndPoints?.Count ?? -1;
        var tcs = new TaskCompletionSource<object>();

        _pendingRequests[messageId.ToString()] = new RequestState(tcs, expectedCount, reply =>
        {
            responses.Add((TReply)reply);
            if (expectedCount > 0 && responses.Count >= expectedCount)
                tcs.TrySetResult(null!);
        });

        headers["RequestMessageId"] = messageId.ToString();

        if (options.EndPoints != null)
        {
            foreach (string endPoint in options.EndPoints)
                await sendAction(typeof(TRequest), messageBytes, headers, endPoint);
        }
        else
        {
            await sendAction(typeof(TRequest), messageBytes, headers, null);
        }

        using var cts = new CancellationTokenSource(options.Timeout);
        cts.Token.Register(() => tcs.TrySetResult(null!)); // timeout returns what we have

        await tcs.Task;
        _pendingRequests.TryRemove(messageId.ToString(), out _);

        return responses;
    }

    public void ProcessReply(string messageId, string messageJson, Type type)
    {
        if (!_pendingRequests.TryGetValue(messageId, out var state))
            return;

        object reply = JsonConvert.DeserializeObject(messageJson, type)!;

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
        Action<object>? OnReply = null);
}
