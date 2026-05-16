using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.ProcessManager.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

var settings = ExampleSettingsLoader.Load();
var workflowQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_WORKFLOW_QUEUE_NAME") ?? "process-manager-orchestrator";
var correlationId = Guid.TryParse(Environment.GetEnvironmentVariable("SC_EXAMPLES_CORRELATION_ID"), out var parsedCorrelationId)
    ? parsedCorrelationId
    : Guid.NewGuid();

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var services = new ServiceCollection();
services.AddSingleton<IReadOnlyList<HandlerReference>>([]);
services.AddExampleBus(settings, "process-manager-starter");

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.SendAsync(
    new OrderSubmitted(correlationId)
    {
        OrderNumber = $"order-{correlationId:N}"
    },
    new SendOptions { EndPoint = workflowQueueName });

ConsoleStatus.Success("process-manager-starter", $"submitted {correlationId}");
await Console.Out.FlushAsync();
