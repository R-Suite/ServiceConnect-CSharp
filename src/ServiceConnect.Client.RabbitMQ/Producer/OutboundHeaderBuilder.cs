using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Builds the outbound header dictionary and <see cref="BasicProperties"/> applied to every
/// message published by <see cref="Producer"/>. Stamps the reserved framework headers
/// (DestinationAddress, MessageId, MessageType, SourceAddress, TimeSent, TypeName,
/// FullTypeName, ConsumerType, Language, optionally SourceMachine) and copies caller-supplied
/// headers underneath them.
/// </summary>
internal sealed class OutboundHeaderBuilder(
    IBusConfiguration busConfiguration,
    IQueueConfiguration queueConfiguration,
    TimeProvider timeProvider,
    ILogger logger)
{
    private const int StampedHeaderCount = 11;

    // Producer-stamped keys: callers cannot override these (the framework owns them).
    // MessageId is deliberately NOT in this set — caller-supplied MessageId (e.g. Bus's
    // authoritative stamp) is preserved by the !ContainsKey check in BuildHeaders below.
    private static readonly HashSet<string> OverwrittenHeaderKeys = new(StringComparer.Ordinal)
    {
        HeaderKeys.DestinationAddress,
        HeaderKeys.MessageType,
        HeaderKeys.SourceAddress,
        HeaderKeys.TimeSent,
        HeaderKeys.SourceMachine,
        HeaderKeys.TypeName,
        HeaderKeys.FullTypeName,
        HeaderKeys.ConsumerType,
        HeaderKeys.Language,
    };

    // Cache (FullName, AssemblyQualifiedName) per Type — these are constant for a given Type.
    // Static so a process running multiple Producer instances pays the reflection cost once.
    private static readonly ConcurrentDictionary<Type, (string FullName, string AQN)> TypeNameCache = new();

    private readonly IBusConfiguration _busConfiguration = busConfiguration;
    private readonly IQueueConfiguration _queueConfiguration = queueConfiguration;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger _logger = logger;

    public Dictionary<string, object?> BuildHeaders(Type type, IReadOnlyDictionary<string, string>? headers, string queueName, string messageType)
    {
        // Build the final object-valued dictionary directly rather than populating a
        // string-valued copy and then rewriting it. Pre-sized to the maximum
        // number of stamped keys + any caller-provided entries.
        var callerCount = headers?.Count ?? 0;
        var result = new Dictionary<string, object?>(callerCount + StampedHeaderCount, StringComparer.Ordinal);

        if (headers is not null)
        {
            foreach (var kvp in headers)
            {
                if (OverwrittenHeaderKeys.Contains(kvp.Key))
                {
                    _logger.LogWarning(
                        "Caller-supplied reserved header '{Key}' will be overwritten by the framework",
                        kvp.Key);
                    continue;
                }
                result[kvp.Key] = kvp.Value;
            }
        }

        result[HeaderKeys.DestinationAddress] = queueName;
        // MessageId is now Bus-authoritative; preserve the Bus-minted value.
        // Only mint one here for callers that invoke the Producer directly (bypassing Bus).
        if (!result.ContainsKey(HeaderKeys.MessageId))
        {
            result[HeaderKeys.MessageId] = Guid.NewGuid().ToString();
        }

        result[HeaderKeys.MessageType] = messageType;

        result[HeaderKeys.SourceAddress] = _queueConfiguration.QueueName;
        result[HeaderKeys.TimeSent] = FormatTimestamp(_timeProvider.GetUtcNow().UtcDateTime);
        if (_busConfiguration.IncludeMachineNameInHeaders)
        {
            result[HeaderKeys.SourceMachine] = Environment.MachineName;
        }

        var (fullName, aqn) = TypeNameCache.GetOrAdd(type, static t => (t.FullName!, t.AssemblyQualifiedName!));
        result[HeaderKeys.TypeName] = fullName;
        result[HeaderKeys.FullTypeName] = aqn;

        result[HeaderKeys.ConsumerType] = "RabbitMQ";
        result[HeaderKeys.Language] = "C#";

        return result;
    }

    /// <summary>
    /// Builds <see cref="BasicProperties"/> by aliasing <paramref name="messageHeaders"/> directly
    /// into <see cref="BasicProperties.Headers"/>. No copy. Callers MUST NOT mutate
    /// <paramref name="messageHeaders"/> while a publish using the returned properties is in flight.
    /// </summary>
    /// <remarks>
    /// The fan-out <c>SendAsync(Type)</c> path re-stamps <c>DestinationAddress</c>,
    /// <c>MessageId</c>, and <c>TimeSent</c> on a single <c>baseHeaders</c> dict between iterations.
    /// Safety relies on publisher confirms (the default since Phase 2; the
    /// <c>PublisherAcknowledgements=false + PublishTimeout&gt;0</c> combo is rejected by the
    /// <see cref="Producer"/> constructor): the prior await on <c>PublishWithTimeoutAsync</c>
    /// returns only after the broker ack, by which time the wire frame is serialised and
    /// RabbitMQ.Client no longer references the dict. The other three publish methods make
    /// <c>BuildBasicProperties</c> the last touch before <c>await PublishWithTimeoutAsync</c>,
    /// so no concurrent mutation is possible there.
    /// </remarks>
    public BasicProperties BuildBasicProperties(Dictionary<string, object?> messageHeaders)
    {
        // Direct assign — Dictionary<string, object?> aligns with BasicProperties.Headers's
        // IDictionary<string, object?> after BuildHeaders' return-type widening. Aliasing
        // is intentional; OutboundHeaderBuilderAliasingTests.BuildBasicProperties_AssignsHeadersDirectly_WithoutCopy
        // asserts the reference identity so a future refactor cannot silently introduce a copy.
        var basicProperties = new BasicProperties
        {
            Headers = messageHeaders,
            Persistent = true
        };

        if (messageHeaders.TryGetValue(HeaderKeys.MessageId, out var messageId))
        {
            basicProperties.MessageId = messageId?.ToString();
        }

        if (messageHeaders.TryGetValue(HeaderKeys.Priority, out var priority))
        {
            try
            {
                basicProperties.Priority = Convert.ToByte(priority, System.Globalization.CultureInfo.InvariantCulture);
            }
            // RabbitMQ priorities are advisory — failing the publish over a misconfigured priority is
            // the wrong default. Soft-drop with enough context that the operator can see which value
            // was bad and why. Catch only the conversion exceptions; anything else propagates.
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                _logger.LogError(
                    ex,
                    "Could not set message priority from value '{Value}' (type '{ValueType}'); priority must be convertible to byte (0..255). Continuing without priority.",
                    priority,
                    priority?.GetType().FullName ?? "<null>");
            }
        }

        return basicProperties;
    }

    // Avoid StringBuilder allocation inside DateTime.ToString("O").
    internal static string FormatTimestamp(DateTime dt)
    {
        Span<char> buffer = stackalloc char[33]; // "O" format max length
        dt.TryFormat(buffer, out int charsWritten, "O");
        return new string(buffer[..charsWritten]);
    }
}
