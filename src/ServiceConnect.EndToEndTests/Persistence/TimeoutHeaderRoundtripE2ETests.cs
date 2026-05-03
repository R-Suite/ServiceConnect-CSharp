using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.EndToEndTests;

/// <summary>
/// End-to-end guard that custom headers carried on the initial process-manager message
/// are captured, persisted alongside the timeout, and re-delivered on the scheduled
/// TimeoutMessage. Header values that arrive from RabbitMQ as byte[] are re-emitted
/// as "base64:..." so receivers can decode them back to the original UTF-8 content.
/// </summary>
[Collection(nameof(MessagingCollection))]
public class TimeoutHeaderRoundtripE2ETests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task TimeoutHeaders_CustomTypedValues_RoundtripFaithfully()
    {
        var queueName = _fixture.GetUniqueQueueName("timeout-header-roundtrip");
        var correlationId = Guid.NewGuid();

        var originalGuid = Guid.NewGuid();
        var originalDateTime = new DateTime(2025, 6, 15, 12, 30, 45, DateTimeKind.Utc);
        var originalDateTimeOffset = new DateTimeOffset(2025, 6, 15, 12, 30, 45, TimeSpan.FromHours(2));
        var originalBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };

        var headersCaptured = new TaskCompletionSource<IReadOnlyDictionary<string, object>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(TimeoutHeaderPmHandler),
                MessageType = typeof(TestMessage)
            },
            new()
            {
                HandlerType = typeof(TimeoutHeaderPmHandler),
                MessageType = typeof(TimeoutMessage)
            }
        };

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<IList<HandlerReference>>(handlerRefs);
                services.AddSingleton(headersCaptured);
                services.AddTransient<IProcessHandler<TimeoutHeaderData, TestMessage>, TimeoutHeaderPmHandler>();
                services.AddTransient<IProcessHandler<TimeoutHeaderData, TimeoutMessage>, TimeoutHeaderPmHandler>();

                services.AddServiceConnect(builder =>
                {
                    builder.UseRabbitMQ(t =>
                    {
                        t.Host = _fixture.RabbitMqHostname;
                        t.Username = _fixture.RabbitMqUsername;
                        t.Password = _fixture.RabbitMqPassword;
                        t.SetClientSetting("Port", _fixture.RabbitMqPort);
                        t.SetClientSetting("RetryCount", 3);
                        t.SetClientSetting("RetrySeconds", 1);
                    });
                    builder.ConfigureQueues(q => q.QueueName = queueName);
                    builder.ConfigureBus(b =>
                    {
                        b.ScanForMessageHandlers = false;
                        b.EnableProcessManagerTimeouts = true;
                        b.ProcessManagerTimeoutPollInterval = TimeSpan.FromMilliseconds(250);
                    });
                    builder.UseInMemoryPersistence();
                });
            })
            .Build();

        try
        {
            await host.StartAsync();

            var bus = host.Services.GetRequiredService<IBus>();

            // Send initial message with custom headers carrying typed values as strings.
            // RabbitMQ will deliver these back to the handler as byte[]; the timeout
            // persistence layer will then re-encode them as "base64:..." strings.
            var initial = new TestMessage(correlationId) { Content = "schedule-typed-timeout" };
            await bus.SendAsync(initial, new SendOptions
            {
                EndPoint = queueName,
                Headers = new Dictionary<string, string>
                {
                    // Guid and date/time encoded as standard ISO formats
                    ["X-Guid"] = originalGuid.ToString("D", CultureInfo.InvariantCulture),
                    ["X-DateTime"] = originalDateTime.ToString("O", CultureInfo.InvariantCulture),
                    ["X-DateTimeOffset"] = originalDateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
                    // byte[] round-trip: sent as base64 string; arrives as byte[] of that string;
                    // re-emitted by BuildOutgoingHeaders as "base64:<base64 of those bytes>"
                    ["X-Bytes"] = Convert.ToBase64String(originalBytes)
                }
            });

            // Wait for the TimeoutMessage handler to capture the re-emitted headers
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            cts.Token.Register(() => headersCaptured.TrySetCanceled());
            var capturedHeaders = await headersCaptured.Task;

            // --- Assertions ---

            // Guid value should survive as a parseable string
            Assert.True(capturedHeaders.ContainsKey("X-Guid"), "X-Guid header missing");
            var guidRaw = DecodeHeader(capturedHeaders["X-Guid"]);
            // May arrive as "base64:..." prefix if stored as byte[]
            var guidStr = StripBase64Prefix(guidRaw);
            var parsedGuid = Guid.Parse(guidStr);
            Assert.Equal(originalGuid, parsedGuid);

            // DateTime value should survive round-trip
            Assert.True(capturedHeaders.ContainsKey("X-DateTime"), "X-DateTime header missing");
            var dtRaw = StripBase64Prefix(DecodeHeader(capturedHeaders["X-DateTime"]));
            var parsedDt = DateTime.Parse(dtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Assert.Equal(originalDateTime, parsedDt);

            // DateTimeOffset value should survive round-trip
            Assert.True(capturedHeaders.ContainsKey("X-DateTimeOffset"), "X-DateTimeOffset header missing");
            var dtoRaw = StripBase64Prefix(DecodeHeader(capturedHeaders["X-DateTimeOffset"]));
            var parsedDto = DateTimeOffset.Parse(dtoRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Assert.Equal(originalDateTimeOffset, parsedDto);

            // Bytes header: must arrive as "base64:..." — strip prefix and decode.
            // "base64:" is the binary header marker used by TimeoutHeaderPersistence.BuildOutgoingHeaders.
            const string binaryHeaderPrefix = "base64:";
            Assert.True(capturedHeaders.ContainsKey("X-Bytes"), "X-Bytes header missing");
            var bytesHeaderRaw = DecodeHeader(capturedHeaders["X-Bytes"]);
            // The value was stored as byte[] (the UTF-8 of the base64 string we sent).
            // BuildOutgoingHeaders re-encodes byte[] as "base64:<base64-of-those-bytes>".
            Assert.StartsWith(binaryHeaderPrefix, bytesHeaderRaw, StringComparison.Ordinal);
            var encodedBytesPayload = bytesHeaderRaw[binaryHeaderPrefix.Length..];
            // The bytes stored were the UTF-8 encoding of the original base64 string we sent.
            // Decode those bytes and then base64-decode them to get back originalBytes.
            var storedBytes = Convert.FromBase64String(encodedBytesPayload);
            var intermediate = Encoding.UTF8.GetString(storedBytes);
            var roundTrippedBytes = Convert.FromBase64String(intermediate);
            Assert.Equal(originalBytes, roundTrippedBytes);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static string DecodeHeader(object value) =>
        value is byte[] b ? Encoding.UTF8.GetString(b) : value?.ToString() ?? string.Empty;

    private static string StripBase64Prefix(string value)
    {
        const string prefix = "base64:";
        return value.StartsWith(prefix, StringComparison.Ordinal)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(value[prefix.Length..]))
            : value;
    }
}

file class TimeoutHeaderData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public bool TimeoutScheduled { get; set; }
}

file class TimeoutHeaderPmHandler(
    TaskCompletionSource<IReadOnlyDictionary<string, object>> headersCaptured) :
    IProcessHandler<TimeoutHeaderData, TestMessage>,
    IProcessHandler<TimeoutHeaderData, TimeoutMessage>
{
    private readonly TaskCompletionSource<IReadOnlyDictionary<string, object>> _headersCaptured = headersCaptured;

    public async Task HandleAsync(TestMessage message, TimeoutHeaderData data, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        data.CorrelationId = message.CorrelationId;
        data.TimeoutScheduled = true;
        // Schedule a short timeout; CaptureForStorage picks up the current consume-context
        // headers (which include our custom X-Guid, X-DateTime, etc. from the initial send)
        await context.Bus.RequestTimeoutAsync(data.CorrelationId, TimeSpan.FromMilliseconds(500));
    }

    public Task HandleAsync(TimeoutMessage message, TimeoutHeaderData data, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        // Capture the headers re-emitted by BuildOutgoingHeaders for assertion
        _headersCaptured.TrySetResult(context.Headers);
        return Task.CompletedTask;
    }
}
