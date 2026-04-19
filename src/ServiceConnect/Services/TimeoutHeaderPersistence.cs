using System.Globalization;
using System.Text;
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
            return persistedHeaders;

        foreach (var header in headers)
        {
            if (ReservedTimeoutHeaders.Contains(header.Key))
                continue;

            persistedHeaders[header.Key] = header.Value;
        }

        return persistedHeaders;
    }

    public static Dictionary<string, string> BuildOutgoingHeaders(IDictionary<string, object> storedHeaders)
    {
        var outgoingHeaders = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var header in storedHeaders)
        {
            if (ReservedTimeoutHeaders.Contains(header.Key))
                continue;

            var converted = ConvertOutgoingHeaderValue(header.Value);
            if (converted != null)
                outgoingHeaders[header.Key] = converted;
        }

        return outgoingHeaders;
    }

    private static string? ConvertOutgoingHeaderValue(object? value) => value switch
    {
        null => string.Empty,
        string stringValue => stringValue,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        bool boolValue => boolValue.ToString(),
        char charValue => charValue.ToString(),
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
        _ => null,
    };
}
