namespace ServiceConnect.Telemetry;

public interface IMessagingSystemAttributes
{
    string MessagingSystem { get; }

    string ProtocolName { get; }
}
