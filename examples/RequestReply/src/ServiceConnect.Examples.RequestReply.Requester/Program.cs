using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.RequestReply.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

var settings = ExampleSettingsLoader.Load();
await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>([]);
services.AddExampleBus(settings, "request-reply-requester");

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
await Task.Delay(1000);

var request = new QuoteRequest(Guid.NewGuid()) { ProductCode = "product-123" };
var response = await bus.SendRequestAsync<QuoteRequest, QuoteResponse>(
    request,
    new RequestOptions { EndPoint = "request-reply-responder", Timeout = 30000 });

ConsoleStatus.Success("request-reply-requester", $"received price {response.Price}");
await Console.Out.FlushAsync();
