using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Moq;
using OpenTelemetry.Trace;
using OpenTelemetry;
using Newtonsoft.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using ServiceConnect.UnitTests.Fakes.Messages;
using ServiceConnect.UnitTests.Fakes.Handlers;

namespace ServiceConnect.UnitTests;

public class TelemetryTests
{
    private static readonly Guid CorrelationId = Guid.NewGuid();

    private readonly Bus sut;

    private readonly Mock<IConfiguration> configurationMock;
    private readonly Mock<IBusContainer> containerMock;
    private readonly Mock<IConsumer> consumerMock;

    private ConsumerEventHandler myEventHandler;

    public TelemetryTests()
    {
        containerMock = new Mock<IBusContainer>();
        consumerMock = new Mock<IConsumer>();

        configurationMock = new Mock<IConfiguration>();
        configurationMock.Setup(c => c.GetLogger()).Returns(new Mock<ILogger>().Object);
        configurationMock.Setup(c => c.GetContainer()).Returns(containerMock.Object);
        configurationMock.Setup(c => c.GetConsumer()).Returns(consumerMock.Object);
        configurationMock.Setup(c => c.GetProcessMessagePipeline(It.IsAny<BusState>())).Returns(new Mock<IProcessMessagePipeline>().Object);
        configurationMock.Setup(c => c.GetSendMessagePipeline()).Returns(new Mock<ISendMessagePipeline>().Object);
        configurationMock.Setup(c => c.AddBusToContainer).Returns(false);
        configurationMock.Setup(c => c.ScanForMesssageHandlers).Returns(false);
        configurationMock.Setup(c => c.AutoStartConsuming).Returns(false);
        configurationMock.Setup(c => c.EnableProcessManagerTimeouts).Returns(false);
        configurationMock.Setup(c => c.TransportSettings.QueueName).Returns("TestQueue");

        sut = new Bus(configurationMock.Object);
    }

    [Fact]
    public void ServiceConnectPublishCommandActivityStartStopTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object);
        FakeMessage1 message = new(CorrelationId);

        sut.Publish(message);

        activityProcessor.Verify(x => x.OnStart(It.IsAny<Activity>()), Times.Once);
        activityProcessor.Verify(x => x.OnEnd(It.IsAny<Activity>()), Times.Once);
    }

    [Fact]
    public void ServiceConnectPublishCommandInstrumentedTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object);
        FakeMessage1 message = new(CorrelationId);

        sut.Publish(message);

        Activity activity = activityProcessor.Invocations[1].Arguments[0] as Activity;
        Assert.Equal(ServiceConnectActivitySource.PublishActivitySourceName, activity?.OperationName);
        Assert.Equal(ActivityKind.Producer, activity?.Kind);
        Assert.Equal("anonymous publish", activity?.DisplayName);
        Assert.Equal("rabbitmq", activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessagingSystem).Value);
        Assert.Equal("amqp", activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.ProtocolName).Value);
        Assert.Equal("publish", activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessagingOperation).Value);
        Assert.Equal("true", activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessagingDestinationAnonymous).Value);
        Assert.Equal(CorrelationId.ToString(), activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessageConversationId).Value);
    }

    [Fact]
    public void ServiceConnectPublishCommandWithHeadersInstrumentedTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object);
        FakeMessage1 message = new(CorrelationId);
        var messageId = Guid.NewGuid().ToString();
        Dictionary<string, string> headers = new()
        {
            { "MessageId", messageId },
        };

        sut.Publish(message, headers);

        Activity activity = activityProcessor.Invocations[1].Arguments[0] as Activity;
        Assert.Equal(messageId, activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessageId).Value);
    }

    [Fact]
    public void ServiceConnectPublishCommandWithRoutingKeyInstrumentedTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object);
        FakeMessage1 message = new(CorrelationId);
        var routingKey = "TestQueue";

        sut.Publish(message, routingKey);

        Activity activity = activityProcessor.Invocations[1].Arguments[0] as Activity;
        Assert.Equal("TestQueue publish", activity?.DisplayName);
        Assert.Equal("TestQueue", activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessagingDestination).Value);
    }

    [Fact]
    public void ServiceConnectPublishCommandWithMessageEnrichmentInstrumentedTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object, options =>
        {
            options.EnrichWithMessage = (activity, message) =>
            {
                _ = activity.SetTag("user.username", (message as FakeMessage1)?.Username.ToString());
            };
        });
        FakeMessage1 message = new(CorrelationId) { Username = "psmith" };

        sut.Publish(message);

        Activity activity = activityProcessor.Invocations[1].Arguments[0] as Activity;
        Assert.Equal("psmith", activity?.Tags.FirstOrDefault(x => x.Key == "user.username").Value);
    }

    [Fact]
    public void ServiceConnectPublishCommandTelemetryDisabledTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object, options =>
        {
            options.EnablePublishTelemetry = false;
        });
        FakeMessage1 message = new(CorrelationId);

        sut.Publish(message);

        activityProcessor.Verify(x => x.OnStart(It.Is<Activity>(a => a.DisplayName.Contains("publish"))), Times.Never);
        activityProcessor.Verify(x => x.OnEnd(It.Is<Activity>(a => a.DisplayName.Contains("publish"))), Times.Never);
    }

    [Fact]
    public async Task ServiceConnectConsumeCommandActivityStartStopTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object);
        var headers = new Dictionary<string, object>
        {
            { "MessageType", Encoding.ASCII.GetBytes("Send") },
        };
        SetupConsumer();
        byte[] message = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new FakeMessage1(CorrelationId)));

        await myEventHandler!(message, typeof(FakeMessage1).AssemblyQualifiedName, headers);

        activityProcessor.Verify(x => x.OnStart(It.IsAny<Activity>()), Times.Once);
        activityProcessor.Verify(x => x.OnEnd(It.IsAny<Activity>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("TestQueue")]
    public async Task ServiceConnectConsumeCommandInstrumentedTest(string destinationAddress)
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object);
        var messageId = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, object>
        {
            { "MessageType", Encoding.ASCII.GetBytes("Send") },
            { "MessageId", Encoding.ASCII.GetBytes(messageId) },
        };
        if (destinationAddress is not null)
        {
            headers["DestinationAddress"] = Encoding.ASCII.GetBytes(destinationAddress);
        }

        SetupConsumer();
        byte[] message = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new FakeMessage1(CorrelationId)));

        await myEventHandler!(message, typeof(FakeMessage1).AssemblyQualifiedName, headers);

        Activity activity = activityProcessor.Invocations[1].Arguments[0] as Activity;
        Assert.Equal(ServiceConnectActivitySource.ConsumeActivitySourceName, activity?.OperationName);
        Assert.Equal(ActivityKind.Consumer, activity?.Kind);
        if (destinationAddress is null)
        {
            Assert.Equal("anonymous receive", activity?.DisplayName);
            Assert.Equal("true", activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessagingDestinationAnonymous).Value);
        }
        else
        {
            Assert.Equal($"{destinationAddress} receive", activity?.DisplayName);
            Assert.Equal(destinationAddress, activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessagingDestination).Value);
        }

        Assert.Equal("rabbitmq", activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessagingSystem).Value);
        Assert.Equal("amqp", activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.ProtocolName).Value);
        Assert.Equal("receive", activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessagingOperation).Value);
        Assert.Equal(messageId, activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessageId).Value);
    }

    [Fact]
    public async Task ServiceConnectConsumeCommandWithMessageBytesEnrichmentInstrumentedTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object, options =>
        {
            options.EnrichWithMessageBytes = (activity, message) =>
            {
                _ = activity.SetTag("user.username", JsonConvert.DeserializeObject<FakeMessage1>(Encoding.UTF8.GetString(message))?.Username.ToString());
            };
        });

        var headers = new Dictionary<string, object>
        {
            { "MessageType", Encoding.ASCII.GetBytes("Send") },
        };

        SetupConsumer();
        byte[] message = Encoding.UTF8.GetBytes(
            JsonConvert.SerializeObject(
                new FakeMessage1(CorrelationId) { Username = "psmith" }));

        await myEventHandler!(message, typeof(FakeMessage1).AssemblyQualifiedName, headers);

        Activity activity = activityProcessor.Invocations[1].Arguments[0] as Activity;
        Assert.Equal("psmith", activity?.Tags.FirstOrDefault(x => x.Key == "user.username").Value);
    }

    [Fact]
    public void ServiceConnectConsumeCommandTelemetryDisabledTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object, options =>
        {
            options.EnableConsumeTelemetry = false;
        });
        FakeMessage1 message = new(CorrelationId);

        sut.Publish(message);

        activityProcessor.Verify(x => x.OnStart(It.Is<Activity>(a => a.DisplayName.Contains("receive"))), Times.Never);
        activityProcessor.Verify(x => x.OnEnd(It.Is<Activity>(a => a.DisplayName.Contains("receive"))), Times.Never);
    }

    [Fact]
    public void ServiceConnectSendCommandActivityStartStopTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object);
        FakeMessage1 message = new(CorrelationId);

        sut.Send(message, headers: null);

        activityProcessor.Verify(x => x.OnStart(It.IsAny<Activity>()), Times.Once);
        activityProcessor.Verify(x => x.OnEnd(It.IsAny<Activity>()), Times.Once);
    }

    [Fact]
    public void ServiceConnectSendCommandWithEndPointActivityStartStopTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object);
        FakeMessage1 message = new(CorrelationId);
        var endPoint = "Endpoint.Test";

        sut.Send(endPoint, message, headers: null);

        activityProcessor.Verify(x => x.OnStart(It.IsAny<Activity>()), Times.Once);
        activityProcessor.Verify(x => x.OnEnd(It.IsAny<Activity>()), Times.Once);
    }

    [Fact]
    public void ServiceConnectSendCommandWithEndPointsActivityStartStopTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object);
        FakeMessage1 message = new(CorrelationId);
        List<string> endPoints = new() { "Endpoint.Test" };

        sut.Send(endPoints, message, headers: null);

        activityProcessor.Verify(x => x.OnStart(It.IsAny<Activity>()), Times.Once);
        activityProcessor.Verify(x => x.OnEnd(It.IsAny<Activity>()), Times.Once);
    }

    [Fact]
    public void ServiceConnectSendCommandInstrumentedTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object);
        FakeMessage1 message = new(CorrelationId);

        sut.Send(message, headers: null);

        Activity activity = activityProcessor.Invocations[1].Arguments[0] as Activity;
        Assert.Equal(ServiceConnectActivitySource.SendActivitySourceName, activity?.OperationName);
        Assert.Equal(ActivityKind.Producer, activity?.Kind);
        Assert.Equal("anonymous publish", activity?.DisplayName);
        Assert.Equal("rabbitmq", activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessagingSystem).Value);
        Assert.Equal("amqp", activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.ProtocolName).Value);
        Assert.Equal("publish", activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessagingOperation).Value);
        Assert.Equal("true", activity?.Tags.FirstOrDefault(x => x.Key == "messaging.destination.anonymous").Value);
        Assert.Equal(CorrelationId.ToString(), activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessageConversationId).Value);
    }

    [Fact]
    public void ServiceConnectSendCommandWithEndPointInstrumentedTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object);
        FakeMessage1 message = new(CorrelationId);
        string endPoint = "Test.Service";

        sut.Send(endPoint, message, headers: null);

        Activity activity = activityProcessor.Invocations[1].Arguments[0] as Activity;
        Assert.Equal($"{endPoint} publish", activity?.DisplayName);
        Assert.Equal(endPoint, activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessagingDestination).Value);
    }

    [Fact]
    public void ServiceConnectSendCommandWithEndPointsInstrumentedTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object);
        FakeMessage1 message = new(CorrelationId);
        List<string> endPoints = new() { "Test.Service1", "Test.Service2" };

        sut.Send(endPoints, message, headers: null);

        Activity activity = activityProcessor.Invocations[1].Arguments[0] as Activity;
        Assert.Equal("[Test.Service1,Test.Service2] publish", activity?.DisplayName);
        Assert.Equal("[Test.Service1,Test.Service2]", activity?.Tags.FirstOrDefault(x => x.Key == MessagingAttributes.MessagingDestination).Value);
    }

    [Fact]
    public void ServiceConnectPublishSendTelemetryDisabledTest()
    {
        var activityProcessor = new Mock<BaseProcessor<Activity>>();
        using var tracer = GetTracer(activityProcessor.Object, options =>
        {
            options.EnableSendTelemetry = false;
        });
        FakeMessage1 message = new(CorrelationId);

        sut.Send(message);

        activityProcessor.Verify(x => x.OnStart(It.Is<Activity>(a => a.DisplayName.Contains("publish"))), Times.Never);
        activityProcessor.Verify(x => x.OnEnd(It.Is<Activity>(a => a.DisplayName.Contains("publish"))), Times.Never);
    }

    private static TracerProvider GetTracer(BaseProcessor<Activity> activityProcessor, Action<ServiceConnectInstrumentationOptions> options = null)
    {
        return Sdk.CreateTracerProviderBuilder()
            .AddProcessor(activityProcessor)
            .ConfigureServices(services =>
            {
                if (options is not null)
                {
                    services.Configure(null, options);
                }
            })
            .AddInstrumentation(sp =>
            {
                var options = sp.GetRequiredService<IOptionsMonitor<ServiceConnectInstrumentationOptions>>().Get(null);
                ServiceConnectActivitySource.Options = options;

                return sp;
            })
            .AddSource(ServiceConnectActivitySource.PublishActivitySourceName)
            .AddSource(ServiceConnectActivitySource.ConsumeActivitySourceName)
            .AddSource(ServiceConnectActivitySource.SendActivitySourceName)
            .Build();
    }

    private void SetupConsumer()
    {
        List<HandlerReference> handlerReferences = new()
        {
            new HandlerReference
            {
                HandlerType = typeof(FakeHandler1),
                MessageType = typeof(FakeMessage1),
            },
        };
        containerMock.Setup(x => x.GetHandlerTypes()).Returns(handlerReferences);
        consumerMock.Setup(x => x.StartConsuming(It.IsAny<string>(), It.IsAny<IList<string>>(), It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<IConfiguration>()));

        sut.StartConsuming();
    }

    private bool AssignEventHandler(ConsumerEventHandler eventHandler)
    {
        myEventHandler = eventHandler;
        return true;
    }
}