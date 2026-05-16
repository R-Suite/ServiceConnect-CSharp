using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.ScatterGather.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

var settings = ExampleSettingsLoader.Load();
var requesterQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_REQUESTER_QUEUE_NAME") ?? "scatter-gather-requester";
var catalogAQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_CATALOG_A_QUEUE_NAME") ?? "scatter-gather-catalog-a";
var catalogBQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_CATALOG_B_QUEUE_NAME") ?? "scatter-gather-catalog-b";
var searchQuery = Environment.GetEnvironmentVariable("SC_EXAMPLES_SEARCH_QUERY") ?? "service-bus";

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var services = new ServiceCollection();
services.AddSingleton<IReadOnlyList<HandlerReference>>([]);
services.AddExampleBus(settings, requesterQueueName);

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
await Task.Delay(1000);

// Scatter-gather uses PublishRequestAsync (broadcast) and collects replies from all
// catalog services that respond within the timeout window. EndPoints fan-out is no
// longer supported on request/reply; broadcast + manual correlation is the right pattern.
var replies = new List<SearchResponse>();
await bus.PublishRequestAsync<SearchRequest, SearchResponse>(
    new SearchRequest(Guid.NewGuid()) { Query = searchQuery },
    reply => { lock (replies) { replies.Add(reply); } },
    new RequestOptions
    {
        ExpectedReplyCount = 2,
        Timeout = 30000
    });

ConsoleStatus.Success("scatter-gather-requester", $"received {replies.Count} replies");
