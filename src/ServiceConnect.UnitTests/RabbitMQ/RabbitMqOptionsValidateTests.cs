using ServiceConnect.Client.RabbitMQ.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class RabbitMqOptionsValidateTests
{
    [Fact]
    public void Validate_AllDefaults_ReturnsEmpty()
    {
        var options = new RabbitMqOptions();
        var errors = options.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_PortBelowOne_ReturnsError()
    {
        var options = new RabbitMqOptions { Port = 0 };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("Port", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PortAbove65535_ReturnsError()
    {
        var options = new RabbitMqOptions { Port = 70000 };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("Port", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NegativeRetryCount_ReturnsError()
    {
        var options = new RabbitMqOptions { RetryCount = -1 };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("RetryCount", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ZeroPublishTimeout_ReturnsError()
    {
        var options = new RabbitMqOptions { PublishTimeout = System.TimeSpan.Zero };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("PublishTimeout", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NegativePublishTimeout_ReturnsError()
    {
        var options = new RabbitMqOptions { PublishTimeout = System.TimeSpan.FromSeconds(-1) };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("PublishTimeout", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ZeroMaxOutstandingPublishConfirms_ReturnsError()
    {
        var options = new RabbitMqOptions { MaxOutstandingPublishConfirms = 0 };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("MaxOutstandingPublishConfirms", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NegativeMessageSize_ReturnsError()
    {
        var options = new RabbitMqOptions { MessageSize = -1 };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("MessageSize", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ZeroNetworkRecoveryInterval_ReturnsError()
    {
        var options = new RabbitMqOptions { NetworkRecoveryInterval = System.TimeSpan.Zero };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("NetworkRecoveryInterval", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AggregatesMultipleErrors()
    {
        var options = new RabbitMqOptions
        {
            Port = 0,
            RetryCount = -5,
            PublishTimeout = System.TimeSpan.Zero,
        };
        var errors = options.Validate();
        Assert.Equal(3, errors.Count);
    }
}
