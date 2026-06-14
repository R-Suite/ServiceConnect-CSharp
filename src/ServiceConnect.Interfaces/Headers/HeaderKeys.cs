namespace ServiceConnect.Interfaces;

/// <summary>
/// Standard header names used by ServiceConnect transports and pipelines.
/// </summary>
public static class HeaderKeys
{
    /// <summary>Header containing the logical message type identifier.</summary>
    public const string MessageType = "MessageType";
    /// <summary>Header containing the full CLR type name.</summary>
    public const string FullTypeName = "FullTypeName";
    /// <summary>Header containing the short CLR type name.</summary>
    public const string TypeName = "TypeName";
    /// <summary>Header containing the routing key used for publish operations.</summary>
    public const string RoutingKey = "RoutingKey";
    /// <summary>Header containing the original source queue or endpoint.</summary>
    public const string SourceAddress = "SourceAddress";
    /// <summary>Header containing the request message id for request/reply flows.</summary>
    public const string RequestMessageId = "RequestMessageId";
    /// <summary>Header containing the response message id for request/reply flows.</summary>
    public const string ResponseMessageId = "ResponseMessageId";
    /// <summary>Header containing the conversation correlation id.</summary>
    public const string CorrelationId = "CorrelationId";
    /// <summary>Header containing the unique message id.</summary>
    public const string MessageId = "MessageId";
    /// <summary>Header indicating whether the broker marked the delivery as redelivered.</summary>
    public const string Redelivered = "Redelivered";
    /// <summary>Header containing the destination queue or endpoint.</summary>
    public const string DestinationAddress = "DestinationAddress";
    /// <summary>Header indicating that a message was published rather than directly sent.</summary>
    public const string Publish = "Publish";
    /// <summary>Header containing the serialized routing slip.</summary>
    public const string RoutingSlip = "RoutingSlip";
    /// <summary>
    /// Header counting the number of routing-slip hops a message has completed.
    /// Incremented authoritatively by the forwarder on each <c>RouteAsync</c> hop;
    /// compared against <c>BusConfiguration.MaxRoutingSlipHops</c> on inbound to defend
    /// against cross-service amplification (service A → [B,C,…32 entries] → service B,
    /// each receiver could otherwise publish a fresh full-cap slip indefinitely).
    /// </summary>
    public const string RoutingSlipHopsCompleted = "RoutingSlipHopsCompleted";
    /// <summary>Header containing the byte-stream sequence identifier.</summary>
    public const string SequenceId = "SequenceId";
    /// <summary>Header containing the current packet number in a stream.</summary>
    public const string PacketNumber = "PacketNumber";
    /// <summary>Header containing the final packet number in a stream.</summary>
    public const string LastPacketNumber = "LastPacketNumber";
    /// <summary>Header indicating that a message belongs to a byte stream.</summary>
    public const string ByteStream = "ByteStream";
    /// <summary>Header containing the time a message was sent.</summary>
    public const string TimeSent = "TimeSent";
    /// <summary>Header containing the time a message was received.</summary>
    public const string TimeReceived = "TimeReceived";
    /// <summary>Header containing the time a message finished processing.</summary>
    public const string TimeProcessed = "TimeProcessed";
    /// <summary>Header containing the sender machine name.</summary>
    public const string SourceMachine = "SourceMachine";
    /// <summary>Header containing the consumer machine name.</summary>
    public const string DestinationMachine = "DestinationMachine";
    /// <summary>Header containing the consuming handler or consumer type.</summary>
    public const string ConsumerType = "ConsumerType";
    /// <summary>Header containing the sender language identifier.</summary>
    public const string Language = "Language";
    /// <summary>Header containing the current retry count.</summary>
    public const string RetryCount = "RetryCount";
    /// <summary>Header containing exception details for failed message processing.</summary>
    public const string Exception = "Exception";
    /// <summary>Header containing message priority metadata.</summary>
    public const string Priority = "Priority";
}
