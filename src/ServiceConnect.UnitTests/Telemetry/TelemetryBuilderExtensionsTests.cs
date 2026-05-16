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
    public void AddTelemetry_OptionsAreFrozenAfterRegistration_MutationThrows()
    {
        var builder = new ServiceConnectBuilder();
        builder.AddTelemetry(o => o.MaxTagValueLength = 100);

        var services = new ServiceCollection();
        foreach (var reg in builder.AdditionalRegistrations)
        {
            reg(services);
        }

        var options = services.BuildServiceProvider().GetRequiredService<ServiceConnectInstrumentationOptions>();

        Assert.Equal(100, options.MaxTagValueLength);
        Assert.Throws<InvalidOperationException>(() => options.MaxTagValueLength = 50);
        Assert.Throws<InvalidOperationException>(() => options.EnablePublishTelemetry = false);
        Assert.Throws<InvalidOperationException>(() => options.EnrichWithMessage = null);
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

        // RabbitMqMessagingSystemAttributes requires ITransportConfiguration to resolve its
        // server.address and server.port values; register a stub so DI can satisfy the ctor.
        services.AddSingleton<ServiceConnect.Interfaces.Configuration.ITransportConfiguration>(
            new StubTransportConfiguration());

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

    private sealed class StubTransportConfiguration : ServiceConnect.Interfaces.Configuration.ITransportConfiguration
    {
        public string Host { get; set; } = "localhost";
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? VirtualHost { get; set; }
        public int RetryDelay { get; set; }
        public int MaxRetries { get; set; }
        public ushort PrefetchCount { get; set; }
        public int GracefulShutdownTimeoutMilliseconds { get; set; }
        public bool SslEnabled { get; set; }
        public bool SuppressPlaintextWarning { get; set; }
        public System.Net.Security.SslPolicyErrors AcceptablePolicyErrors { get; set; }
        public string? ServerName { get; set; }
        public string? CertPath { get; set; }
        public string? CertPassphrase { get; set; }
        public System.Security.Cryptography.X509Certificates.X509CertificateCollection? Certs { get; set; }
        public System.Security.Authentication.SslProtocols SslProtocol { get; set; }
        public System.Net.Security.LocalCertificateSelectionCallback? CertificateSelectionCallback { get; set; }
        public System.Net.Security.RemoteCertificateValidationCallback? CertificateValidationCallback { get; set; }
        public IReadOnlyDictionary<string, object> ClientSettings { get; } = new Dictionary<string, object>();
        public void SetClientSetting(string key, object value) { }
    }
}
