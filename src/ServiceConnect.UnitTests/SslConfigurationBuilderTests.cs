using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

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

        var result = SslConfigurationBuilder.BuildSslOptions(mock.Object);

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

        var result = SslConfigurationBuilder.BuildSslOptions(mock.Object);

        Assert.True(result.Enabled);
    }

    [Fact]
    public void BuildSslOptions_SetsCertificateCallbacks()
    {
        LocalCertificateSelectionCallback selectionCallback = (_, _, _, _, _) => null!;
        RemoteCertificateValidationCallback validationCallback = (_, _, _, _) => true;

        var mock = new Mock<ITransportConfiguration>();
        mock.Setup(t => t.CertificateSelectionCallback).Returns(selectionCallback);
        mock.Setup(t => t.CertificateValidationCallback).Returns(validationCallback);

        var result = SslConfigurationBuilder.BuildSslOptions(mock.Object);

        Assert.Same(selectionCallback, result.CertificateSelectionCallback);
        Assert.Same(validationCallback, result.CertificateValidationCallback);
    }

    [Fact]
    public void BuildSslOptions_SetsCertificateCollection()
    {
        var certs = new X509CertificateCollection();
        var mock = new Mock<ITransportConfiguration>();
        mock.Setup(t => t.Certs).Returns(certs);

        var result = SslConfigurationBuilder.BuildSslOptions(mock.Object);

        Assert.Same(certs, result.Certs);
    }
}
