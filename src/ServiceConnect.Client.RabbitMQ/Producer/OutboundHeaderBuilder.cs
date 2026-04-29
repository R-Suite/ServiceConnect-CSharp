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

    // Cache (FullName, AssemblyQualifiedName) per Type — these are constant for a given Type.
    // Static so a process running multiple Producer instances pays the reflection cost once.
    private static readonly ConcurrentDictionary<Type, (string FullName, string AQN)> TypeNameCache = new();

    private readonly IBusConfiguration _busConfiguration = busConfiguration;
    private readonly IQueueConfiguration _queueConfiguration = queueConfiguration;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger _logger = logger;

    public Dictionary<string, object> BuildHeaders(Type type, IDictionary<string, string>? headers, string queueName, string messageType)
    {
        // Build the final object-valued dictionary directly rather than populating a
        // string-valued copy and then rewriting it. Pre-sized to the maximum
        // number of stamped keys + any caller-provided entries.
        var callerCount = headers?.Count ?? 0;
        var result = new Dictionary<string, object>(callerCount + StampedHeaderCount, StringComparer.Ordinal);

        if (headers is not null)
        {
            foreach (var kvp in headers)
            {
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

    public BasicProperties BuildBasicProperties(Dictionary<string, object> messageHeaders)
    {
        // foreach avoids the LINQ Select + enumerator allocation per message.
        var headersCopy = new Dictionary<string, object?>(messageHeaders.Count, StringComparer.Ordinal);
        foreach (var kvp in messageHeaders)
        {
            headersCopy[kvp.Key] = kvp.Value;
        }

        var basicProperties = new BasicProperties
        {
            Headers = headersCopy,
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
    private static string FormatTimestamp(DateTime dt)
    {
        Span<char> buffer = stackalloc char[33]; // "O" format max length
        dt.TryFormat(buffer, out int charsWritten, "O");
        return new string(buffer[..charsWritten]);
    }
}
