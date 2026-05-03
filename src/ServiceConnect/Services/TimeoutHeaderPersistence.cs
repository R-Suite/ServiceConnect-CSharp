using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

internal static class TimeoutHeaderPersistence
{
    private static readonly HashSet<string> ReservedTimeoutHeaders =
    [
        HeaderKeys.MessageType,
        HeaderKeys.TypeName,
        HeaderKeys.FullTypeName,
        HeaderKeys.MessageId,
        HeaderKeys.DestinationAddress,
        HeaderKeys.SourceAddress,
        HeaderKeys.RequestMessageId,
        HeaderKeys.ResponseMessageId,
        HeaderKeys.RoutingKey,
        HeaderKeys.RoutingSlip,
        HeaderKeys.Publish,
        HeaderKeys.SequenceId,
        HeaderKeys.PacketNumber,
        HeaderKeys.LastPacketNumber,
        HeaderKeys.ByteStream,
        HeaderKeys.TimeSent,
        HeaderKeys.TimeReceived,
        HeaderKeys.TimeProcessed,
        HeaderKeys.SourceMachine,
        HeaderKeys.DestinationMachine,
        HeaderKeys.Redelivered,
        HeaderKeys.ConsumerType,
        HeaderKeys.Language,
        HeaderKeys.Exception,
    ];

    public static Dictionary<string, object> CaptureForStorage(IReadOnlyDictionary<string, object>? headers)
    {
        var persistedHeaders = new Dictionary<string, object>(StringComparer.Ordinal);

        if (headers is null)
        {
            return persistedHeaders;
        }

        foreach (var header in headers)
        {
            if (ReservedTimeoutHeaders.Contains(header.Key))
            {
                continue;
            }

            persistedHeaders[header.Key] = header.Value;
        }

        return persistedHeaders;
    }

    public static Dictionary<string, string> BuildOutgoingHeaders(IReadOnlyDictionary<string, object> storedHeaders, ILogger? logger = null)
    {
        var outgoingHeaders = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var header in storedHeaders)
        {
            if (ReservedTimeoutHeaders.Contains(header.Key))
            {
                continue;
            }

            var converted = ConvertOutgoingHeaderValue(header.Value);
            if (converted != null)
            {
                outgoingHeaders[header.Key] = converted;
            }
            else if (logger is not null && logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Dropping timeout header {HeaderKey} with unsupported value type {ValueType}",
                    header.Key,
                    header.Value?.GetType().FullName ?? "<null>");
            }
        }

        return outgoingHeaders;
    }

    /// <summary>
    /// Prefix applied to base64-encoded binary header values so receivers can recognise
    /// them as round-trippable binary rather than arbitrary text. Outgoing headers are
    /// <c>string</c>-typed, so without a marker a consumer cannot tell a text header
    /// from a binary one.
    /// </summary>
    public const string BinaryHeaderPrefix = "base64:";

    private static string? ConvertOutgoingHeaderValue(object? value) => value switch
    {
        null => string.Empty,
        string stringValue => stringValue,
        // Binary values: encode as base64 with a reserved marker prefix so the
        // receiver can distinguish them from text and decode round-trip-safely.
        // Prior behaviour (UTF-8 GetString) silently corrupted non-text bytes.
        byte[] bytes => BinaryHeaderPrefix + Convert.ToBase64String(bytes),
        bool boolValue => boolValue.ToString(CultureInfo.InvariantCulture),
        char charValue => charValue.ToString(CultureInfo.InvariantCulture),
        byte byteValue => byteValue.ToString(CultureInfo.InvariantCulture),
        sbyte sbyteValue => sbyteValue.ToString(CultureInfo.InvariantCulture),
        short shortValue => shortValue.ToString(CultureInfo.InvariantCulture),
        ushort ushortValue => ushortValue.ToString(CultureInfo.InvariantCulture),
        int intValue => intValue.ToString(CultureInfo.InvariantCulture),
        uint uintValue => uintValue.ToString(CultureInfo.InvariantCulture),
        long longValue => longValue.ToString(CultureInfo.InvariantCulture),
        ulong ulongValue => ulongValue.ToString(CultureInfo.InvariantCulture),
        float floatValue => floatValue.ToString(CultureInfo.InvariantCulture),
        double doubleValue => doubleValue.ToString(CultureInfo.InvariantCulture),
        decimal decimalValue => decimalValue.ToString(CultureInfo.InvariantCulture),
        Guid guidValue => guidValue.ToString("D", CultureInfo.InvariantCulture),
        DateTime dateTimeValue => dateTimeValue.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffsetValue => dateTimeOffsetValue.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan timeSpanValue => timeSpanValue.ToString("c", CultureInfo.InvariantCulture),
        _ => null,
    };
}
