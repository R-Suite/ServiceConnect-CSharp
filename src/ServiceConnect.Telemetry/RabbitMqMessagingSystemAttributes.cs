namespace ServiceConnect.Telemetry;

public sealed class RabbitMqMessagingSystemAttributes : IMessagingSystemAttributes
{
    public string MessagingSystem => "rabbitmq";

    public string ProtocolName => "amqp";
}
