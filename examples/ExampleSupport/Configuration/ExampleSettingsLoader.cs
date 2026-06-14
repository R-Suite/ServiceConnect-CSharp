using Microsoft.Extensions.Configuration;

namespace ServiceConnect.Examples.Support.Configuration;

public static class ExampleSettingsLoader
{
    public static ExampleSettings Load()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddEnvironmentVariables(prefix: "SC_EXAMPLES_")
            .Build();

        return new ExampleSettings
        {
            RabbitMqHost = GetString(config, "RabbitMqHost") ?? "localhost",
            RabbitMqPort = GetInt(config, "RabbitMqPort") ?? 5672,
            RabbitMqUsername = GetString(config, "RabbitMqUsername") ?? "guest",
            RabbitMqPassword = GetString(config, "RabbitMqPassword") ?? "guest",
            MongoConnectionString = GetString(config, "MongoConnectionString") ?? "mongodb://localhost:27017"
        };
    }

    private static string? GetString(IConfiguration configuration, string key)
    {
        return configuration[$"Examples:{key}"] ?? configuration[key];
    }

    private static int? GetInt(IConfiguration configuration, string key)
    {
        var value = GetString(configuration, key);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }
}
