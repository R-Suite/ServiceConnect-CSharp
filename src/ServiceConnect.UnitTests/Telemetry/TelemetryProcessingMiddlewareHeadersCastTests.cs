using System.Collections;
using System.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

[Collection("ActivityListener")]
public sealed class TelemetryProcessingMiddlewareHeadersCastTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ServiceConnectInstrumentationOptions _options = new() { EnableConsumeTelemetry = true };
    private readonly IMessagingSystemAttributes _attrs = new RabbitMqMessagingSystemAttributes();

    public TelemetryProcessingMiddlewareHeadersCastTests()
    {
        // Active listener is required by IsConsumeTelemetryEnabled — without it the
        // middleware short-circuits and never exercises the Headers materialisation.
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>
    /// A deliberately-degenerate <see cref="IDictionary{TKey,TValue}"/> impl that does NOT also
    /// implement <see cref="IReadOnlyDictionary{TKey,TValue}"/>. The pre-fix middleware downcast
    /// <c>(IReadOnlyDictionary&lt;string,object&gt;)</c> would throw <see cref="InvalidCastException"/>
    /// on this; the post-fix defensive copy succeeds.
    /// </summary>
    private sealed class WriteOnlyDictionaryAdapter : IDictionary<string, object>
    {
        private readonly Dictionary<string, object> _inner = [];
        public object this[string key] { get => _inner[key]; set => _inner[key] = value; }
        public ICollection<string> Keys => _inner.Keys;
        public ICollection<object> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool IsReadOnly => false;
        public void Add(string key, object value) => _inner.Add(key, value);
        public void Add(KeyValuePair<string, object> item) => _inner.Add(item.Key, item.Value);
        public void Clear() => _inner.Clear();
        public bool Contains(KeyValuePair<string, object> item) => ((IDictionary<string, object>)_inner).Contains(item);
        public bool ContainsKey(string key) => _inner.ContainsKey(key);
        public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex) => ((IDictionary<string, object>)_inner).CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _inner.GetEnumerator();
        public bool Remove(string key) => _inner.Remove(key);
        public bool Remove(KeyValuePair<string, object> item) => ((IDictionary<string, object>)_inner).Remove(item);
        public bool TryGetValue(string key, out object value) => _inner.TryGetValue(key, out value!);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task ProcessAsync_WithThirdPartyHeadersDictionary_DoesNotThrowInvalidCastException()
    {
        var middleware = new TelemetryProcessingMiddleware(_options, _attrs);

        IDictionary<string, object> headers = new WriteOnlyDictionaryAdapter
        {
            ["X-Test"] = "value",
        };
        var envelope = new Envelope
        {
            Body = new ReadOnlyMemory<byte>([1, 2, 3]),
            Headers = headers,
        };
        var nextCalled = false;
        Task<ConsumeEventResult> Next(
            ReadOnlyMemory<byte> bytes, Type type, object msg,
            IDictionary<string, object> hdrs, Envelope env, CancellationToken ct)
        {
            nextCalled = true;
            return Task.FromResult(new ConsumeEventResult { Success = true });
        }

        // Pre-fix: would throw InvalidCastException because WriteOnlyDictionaryAdapter
        // is not assignable to IReadOnlyDictionary<string, object>.
        var result = await middleware.ProcessAsync(
            envelope.Body,
            typeof(string),
            "msg",
            headers,
            envelope,
            Next,
            CancellationToken.None);

        Assert.True(nextCalled);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task ProcessAsync_WithStandardDictionary_StillBehavesAsBefore()
    {
        var middleware = new TelemetryProcessingMiddleware(_options, _attrs);

        var headers = new Dictionary<string, object> { ["MessageId"] = "id-1" };
        var envelope = new Envelope
        {
            Body = new ReadOnlyMemory<byte>([9]),
            Headers = headers,
        };

        static Task<ConsumeEventResult> Next(
            ReadOnlyMemory<byte> bytes, Type type, object msg,
            IDictionary<string, object> hdrs, Envelope env, CancellationToken ct) =>
            Task.FromResult(new ConsumeEventResult { Success = true });

        var result = await middleware.ProcessAsync(
            envelope.Body,
            typeof(string),
            "msg",
            headers,
            envelope,
            Next,
            CancellationToken.None);

        Assert.True(result.Success);
    }
}
