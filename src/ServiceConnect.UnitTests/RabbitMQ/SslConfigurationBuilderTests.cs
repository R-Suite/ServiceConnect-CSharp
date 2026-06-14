using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class SslConfigurationBuilderTests
{
    [Fact]
    public void BuildSslOptions_SetsAllProperties()
    {
        var mock = new Mock<ITransportConfiguration>();
        mock.Setup(t => t.SslProtocol).Returns(SslProtocols.Tls12);
        mock.Setup(t => t.ServerName).Returns("myserver");
        mock.Setup(t => t.CertPath).Returns("/path/to/cert.pem");
        mock.Setup(t => t.CertPassphrase).Returns("secret");
        mock.Setup(t => t.AcceptablePolicyErrors).Returns(SslPolicyErrors.RemoteCertificateNameMismatch);
        mock.Setup(t => t.Certs).Returns((X509CertificateCollection?)null);

        var result = SslConfigurationBuilder.BuildSslOptions(mock.Object, NullLogger.Instance);

        Assert.Equal(SslProtocols.Tls12, result.Version);
        Assert.Equal("myserver", result.ServerName);
        Assert.Equal("/path/to/cert.pem", result.CertPath);
        Assert.Equal("secret", result.CertPassphrase);
        Assert.Equal(SslPolicyErrors.RemoteCertificateNameMismatch, result.AcceptablePolicyErrors);
    }

    [Fact]
    public void BuildSslOptions_EnabledIsAlwaysTrue()
    {
        var mock = new Mock<ITransportConfiguration>();
        mock.Setup(t => t.SslEnabled).Returns(false);
        mock.Setup(t => t.SslProtocol).Returns(SslProtocols.Tls12);
        mock.Setup(t => t.ServerName).Returns("localhost");

        var result = SslConfigurationBuilder.BuildSslOptions(mock.Object, NullLogger.Instance);

        Assert.True(result.Enabled);
    }

    [Fact]
    public void BuildSslOptions_SetsCertificateCallbacks()
    {
        LocalCertificateSelectionCallback selectionCallback = (_, _, _, _, _) => null!;
        RemoteCertificateValidationCallback validationCallback = (_, _, _, _) => true;

        var mock = new Mock<ITransportConfiguration>();
        mock.Setup(t => t.ServerName).Returns("localhost");
        mock.Setup(t => t.CertificateSelectionCallback).Returns(selectionCallback);
        mock.Setup(t => t.CertificateValidationCallback).Returns(validationCallback);

        var result = SslConfigurationBuilder.BuildSslOptions(mock.Object, NullLogger.Instance);

        Assert.Same(selectionCallback, result.CertificateSelectionCallback);
        Assert.Same(validationCallback, result.CertificateValidationCallback);
    }

    [Fact]
    public void BuildSslOptions_ThrowsWhenServerNameIsNull()
    {
        var mock = new Mock<ITransportConfiguration>();
        mock.Setup(t => t.ServerName).Returns((string?)null);

        Assert.Throws<ArgumentException>(() => SslConfigurationBuilder.BuildSslOptions(mock.Object, NullLogger.Instance));
    }

    [Fact]
    public void BuildSslOptions_ThrowsWhenServerNameIsEmpty()
    {
        var mock = new Mock<ITransportConfiguration>();
        mock.Setup(t => t.ServerName).Returns(string.Empty);

        Assert.Throws<ArgumentException>(() => SslConfigurationBuilder.BuildSslOptions(mock.Object, NullLogger.Instance));
    }

    [Fact]
    public void BuildSslOptions_SetsCertificateCollection()
    {
        var certs = new X509CertificateCollection();
        var mock = new Mock<ITransportConfiguration>();
        mock.Setup(t => t.ServerName).Returns("localhost");
        mock.Setup(t => t.Certs).Returns(certs);

        var result = SslConfigurationBuilder.BuildSslOptions(mock.Object, NullLogger.Instance);

        Assert.Same(certs, result.Certs);
    }

    [Fact]
    public void BuildSslOptions_NonZeroAcceptablePolicyErrors_LogsWarning()
    {
        var fakeLogger = new FakeLogger<SslConfigurationBuilderTag>();
        var mock = new Mock<ITransportConfiguration>();
        mock.Setup(t => t.ServerName).Returns("broker.example.com");
        mock.Setup(t => t.AcceptablePolicyErrors).Returns(SslPolicyErrors.RemoteCertificateChainErrors);

        _ = SslConfigurationBuilder.BuildSslOptions(mock.Object, fakeLogger);

        Assert.Contains(fakeLogger.Collector.GetSnapshot(),
            r => r.Level == LogLevel.Warning
              && r.Message.Contains("AcceptablePolicyErrors", StringComparison.Ordinal)
              && r.Message.Contains("RemoteCertificateChainErrors", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildSslOptions_DefaultPolicyErrors_NoWarning()
    {
        var fakeLogger = new FakeLogger<SslConfigurationBuilderTag>();
        var mock = new Mock<ITransportConfiguration>();
        mock.Setup(t => t.ServerName).Returns("broker.example.com");
        mock.Setup(t => t.AcceptablePolicyErrors).Returns(SslPolicyErrors.None);

        _ = SslConfigurationBuilder.BuildSslOptions(mock.Object, fakeLogger);

        Assert.DoesNotContain(fakeLogger.Collector.GetSnapshot(),
            r => r.Level == LogLevel.Warning);
    }

    [Fact]
    public void BuildSslOptions_CustomValidationCallback_LogsWarning()
    {
        var fakeLogger = new FakeLogger<SslConfigurationBuilderTag>();
        static bool AlwaysAccept(object _, System.Security.Cryptography.X509Certificates.X509Certificate? __, System.Security.Cryptography.X509Certificates.X509Chain? ___, SslPolicyErrors ____) => true;
        var mock = new Mock<ITransportConfiguration>();
        mock.Setup(t => t.ServerName).Returns("broker.example.com");
        mock.Setup(t => t.CertificateValidationCallback).Returns(AlwaysAccept);

        _ = SslConfigurationBuilder.BuildSslOptions(mock.Object, fakeLogger);

        Assert.Contains(fakeLogger.Collector.GetSnapshot(),
            r => r.Level == LogLevel.Warning
              && r.Message.Contains("CertificateValidationCallback", StringComparison.Ordinal));
    }

    /// <summary>Placeholder type so FakeLogger has a category.</summary>
    public sealed class SslConfigurationBuilderTag { }
}
