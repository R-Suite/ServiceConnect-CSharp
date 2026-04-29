using Microsoft.Extensions.DependencyInjection;
using ServiceConnect;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

public sealed class TelemetryBuilderExtensionsTests
{
    [Fact]
    public void AddTelemetry_RegistersMiddlewareAtPositionZeroInBothPipelines()
    {
        var builder = new ServiceConnectBuilder();
        builder.ConfigurePipeline(p =>
        {
            p.SendMessageMiddleware.Add(typeof(DummySend));
            p.MessageProcessingMiddleware.Add(typeof(DummyProcess));
        });

        builder.AddTelemetry();

        Assert.Equal(typeof(TelemetrySendMiddleware), builder.BusConfig.Pipeline.SendMessageMiddleware[0]);
        Assert.Equal(typeof(TelemetryProcessingMiddleware), builder.BusConfig.Pipeline.MessageProcessingMiddleware[0]);

        var services = new ServiceCollection();
        foreach (var reg in builder.AdditionalRegistrations)
        {
            reg(services);
        }

        var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<TelemetrySendMiddleware>(),
            provider.GetRequiredService<TelemetrySendMiddleware>());

        Assert.Same(
            provider.GetRequiredService<TelemetryProcessingMiddleware>(),
            provider.GetRequiredService<TelemetryProcessingMiddleware>());
    }

    [Fact]
    public void AddTelemetry_InvokesConfigureCallbackOnOptions()
    {
        var builder = new ServiceConnectBuilder();

        builder.AddTelemetry(opts => opts.EnablePublishTelemetry = false);

        var services = new ServiceCollection();
        foreach (var reg in builder.AdditionalRegistrations)
        {
            reg(services);
        }

        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<ServiceConnectInstrumentationOptions>();
        Assert.False(options.EnablePublishTelemetry);
    }

    [Fact]
    public void AddTelemetry_TwoBuilders_ProduceDistinctOptionsInstances()
    {
        var builderA = new ServiceConnectBuilder();
        var builderB = new ServiceConnectBuilder();

        builderA.AddTelemetry(o => o.EnablePublishTelemetry = true);
        builderB.AddTelemetry(o => o.EnablePublishTelemetry = false);

        var servicesA = new ServiceCollection();
        foreach (var reg in builderA.AdditionalRegistrations)
        {
            reg(servicesA);
        }

        var optionsA = servicesA.BuildServiceProvider().GetRequiredService<ServiceConnectInstrumentationOptions>();

        var servicesB = new ServiceCollection();
        foreach (var reg in builderB.AdditionalRegistrations)
        {
            reg(servicesB);
        }

        var optionsB = servicesB.BuildServiceProvider().GetRequiredService<ServiceConnectInstrumentationOptions>();

        Assert.NotSame(optionsA, optionsB);
        Assert.True(optionsA.EnablePublishTelemetry);
        Assert.False(optionsB.EnablePublishTelemetry);
    }

    [Fact]
    public void AddTelemetry_UserRegisteredAttributes_WinOverDefault()
    {
        var builder = new ServiceConnectBuilder();

        var customAttrs = new TestKafkaMessagingSystemAttributes();
        builder.AddRegistration(s => s.AddSingleton<IMessagingSystemAttributes>(customAttrs));

        builder.AddTelemetry();

        var services = new ServiceCollection();
        foreach (var reg in builder.AdditionalRegistrations)
        {
            reg(services);
        }

        var resolved = services.BuildServiceProvider().GetRequiredService<IMessagingSystemAttributes>();
        Assert.Same(customAttrs, resolved);
    }

    [Fact]
    public void AddTelemetry_NoUserAttributesRegistration_DefaultsToRabbitMq()
    {
        var builder = new ServiceConnectBuilder();
        builder.AddTelemetry();

        var services = new ServiceCollection();
        foreach (var reg in builder.AdditionalRegistrations)
        {
            reg(services);
        }

        var resolved = services.BuildServiceProvider().GetRequiredService<IMessagingSystemAttributes>();
        Assert.IsType<RabbitMqMessagingSystemAttributes>(resolved);
    }

    private sealed class DummySend : ISendMessageMiddleware
    {
        public Task ProcessAsync(SendContext context, SendMessageDelegate next, CancellationToken cancellationToken)
            => next(context, cancellationToken);
    }

    private sealed class DummyProcess : IMessageProcessingMiddleware
    {
        public Task<ConsumeEventResult> ProcessAsync(
            ReadOnlyMemory<byte> messageBytes, Type messageType, object message,
            IDictionary<string, object> headers, Envelope envelope,
            MessageProcessingDelegate next,
            CancellationToken cancellationToken)
            => next(messageBytes, messageType, message, headers, envelope, cancellationToken);
    }

    private sealed class TestKafkaMessagingSystemAttributes : IMessagingSystemAttributes
    {
        public string MessagingSystem => "kafka";
        public string ProtocolName => "kafka";
    }
}
