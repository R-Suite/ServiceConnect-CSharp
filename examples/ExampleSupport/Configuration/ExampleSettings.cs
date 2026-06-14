namespace ServiceConnect.Examples.Support.Configuration;

public sealed class ExampleSettings
{
    public string RabbitMqHost { get; init; } = "localhost";
    public int RabbitMqPort { get; init; } = 5672;
    public string RabbitMqUsername { get; init; } = "guest";
    public string RabbitMqPassword { get; init; } = "guest";
    public string MongoConnectionString { get; init; } = "mongodb://localhost:27017";
}
