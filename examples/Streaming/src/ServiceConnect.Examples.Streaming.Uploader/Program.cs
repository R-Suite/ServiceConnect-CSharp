using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using ServiceConnect.Examples.Streaming.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
var endpointName = Environment.GetEnvironmentVariable("SC_EXAMPLES_ENDPOINT_NAME") ?? "streaming-receiver";

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
services.AddExampleBus(settings, "streaming-uploader");

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();

var document = new DocumentUploaded(Guid.NewGuid())
{
    FileName = "demo-document.txt",
    TotalBytes = 56
};

var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(document));
var chunkSize = payload.Length / 3;

await using var stream = bus.CreateStream<DocumentUploaded>(endpointName);
await stream.WriteAsync(payload, 0, chunkSize);
await stream.WriteAsync(payload, chunkSize, chunkSize);
await stream.WriteAsync(payload, chunkSize * 2, payload.Length - (chunkSize * 2));
await stream.CloseAsync();

ConsoleStatus.Success("streaming-uploader", "sent 3 chunks for demo-document.txt");
await Console.Out.FlushAsync();
