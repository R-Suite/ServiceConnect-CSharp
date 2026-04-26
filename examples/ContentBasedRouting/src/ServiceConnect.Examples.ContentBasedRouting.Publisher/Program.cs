using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.ContentBasedRouting.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
var premiumOrderId = Environment.GetEnvironmentVariable("SC_EXAMPLES_PREMIUM_ORDER_ID") ?? "premium-order-100";
var standardOrderId = Environment.GetEnvironmentVariable("SC_EXAMPLES_STANDARD_ORDER_ID") ?? "standard-order-200";

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>([]);
services.AddExampleBus(settings, "content-based-routing-publisher");

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.PublishAsync(new PremiumOrderPlaced(Guid.NewGuid()) { OrderId = premiumOrderId });
await bus.PublishAsync(new StandardOrderPlaced(Guid.NewGuid()) { OrderId = standardOrderId });
ConsoleStatus.Success("content-based-routing-publisher", $"published {premiumOrderId} and {standardOrderId}");
await Console.Out.FlushAsync();
