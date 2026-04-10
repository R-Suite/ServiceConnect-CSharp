using System.Net.Security;
using System.Security.Authentication;
using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class TransportConfigurationTests
    {
        [Fact]
        public void DefaultHostIsLocalhost()
        {
            var config = new TransportConfiguration();
            Assert.Equal("localhost", config.Host);
        }

        [Fact]
        public void DefaultUsernameIsNull()
        {
            var config = new TransportConfiguration();
            Assert.Null(config.Username);
        }

        [Fact]
        public void DefaultPasswordIsNull()
        {
            var config = new TransportConfiguration();
            Assert.Null(config.Password);
        }

        [Fact]
        public void DefaultVirtualHostIsNull()
        {
            var config = new TransportConfiguration();
            Assert.Null(config.VirtualHost);
        }

        [Fact]
        public void DefaultRetryDelayIs3000()
        {
            var config = new TransportConfiguration();
            Assert.Equal(3000, config.RetryDelay);
        }

        [Fact]
        public void DefaultMaxRetriesIs3()
        {
            var config = new TransportConfiguration();
            Assert.Equal(3, config.MaxRetries);
        }

        [Fact]
        public void DefaultPrefetchCountIs1()
        {
            var config = new TransportConfiguration();
            Assert.Equal((ushort)1, config.PrefetchCount);
        }

        [Fact]
        public void DefaultSslEnabledIsFalse()
        {
            var config = new TransportConfiguration();
            Assert.False(config.SslEnabled);
        }

        [Fact]
        public void DefaultAcceptablePolicyErrorsIsNone()
        {
            var config = new TransportConfiguration();
            Assert.Equal(SslPolicyErrors.None, config.AcceptablePolicyErrors);
        }

        [Fact]
        public void DefaultServerNameIsNull()
        {
            var config = new TransportConfiguration();
            Assert.Null(config.ServerName);
        }

        [Fact]
        public void DefaultCertPathIsNull()
        {
            var config = new TransportConfiguration();
            Assert.Null(config.CertPath);
        }

        [Fact]
        public void DefaultCertPassphraseIsNull()
        {
            var config = new TransportConfiguration();
            Assert.Null(config.CertPassphrase);
        }

        [Fact]
        public void DefaultCertsIsNull()
        {
            var config = new TransportConfiguration();
            Assert.Null(config.Certs);
        }

        [Fact]
        public void DefaultSslProtocolIsTls12()
        {
            var config = new TransportConfiguration();
            Assert.Equal(SslProtocols.Tls12, config.SslProtocol);
        }

        [Fact]
        public void DefaultCertificateSelectionCallbackIsNull()
        {
            var config = new TransportConfiguration();
            Assert.Null(config.CertificateSelectionCallback);
        }

        [Fact]
        public void DefaultCertificateValidationCallbackIsNull()
        {
            var config = new TransportConfiguration();
            Assert.Null(config.CertificateValidationCallback);
        }

        [Fact]
        public void DefaultClientSettingsIsEmptyDictionary()
        {
            var config = new TransportConfiguration();
            Assert.NotNull(config.ClientSettings);
            Assert.Empty(config.ClientSettings);
        }

        [Fact]
        public void PropertiesAreSettable()
        {
            var config = new TransportConfiguration
            {
                Host = "rabbitmq-host",
                Username = "admin",
                Password = "secret",
                VirtualHost = "/myapp",
                RetryDelay = 5000,
                MaxRetries = 10,
                PrefetchCount = 5,
                SslEnabled = true,
                ServerName = "myserver"
            };

            Assert.Equal("rabbitmq-host", config.Host);
            Assert.Equal("admin", config.Username);
            Assert.Equal("secret", config.Password);
            Assert.Equal("/myapp", config.VirtualHost);
            Assert.Equal(5000, config.RetryDelay);
            Assert.Equal(10, config.MaxRetries);
            Assert.Equal((ushort)5, config.PrefetchCount);
            Assert.True(config.SslEnabled);
            Assert.Equal("myserver", config.ServerName);
        }

        [Fact]
        public void ClientSettingsCanStoreValues()
        {
            var config = new TransportConfiguration();
            config.ClientSettings["key1"] = "value1";
            config.ClientSettings["key2"] = 42;

            Assert.Equal(2, config.ClientSettings.Count);
            Assert.Equal("value1", config.ClientSettings["key1"]);
            Assert.Equal(42, config.ClientSettings["key2"]);
        }
    }
}
