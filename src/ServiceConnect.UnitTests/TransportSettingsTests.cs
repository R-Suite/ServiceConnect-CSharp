// Copyright (C) 2015 Timothy Watson, Jakub Pachansky

// This program is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; either version 2
// of the License, or (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA 02110-1301, USA.

using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ServiceConnect.Core;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class TransportSettingsTests
    {
        [Fact]
        public void ShouldHaveDefaultValues()
        {
            // Arrange & Act
            var settings = new TransportSettings();

            // Assert
            Assert.Equal(0, settings.RetryDelay);
            Assert.Equal(0, settings.MaxRetries);
            Assert.Null(settings.Host);
            Assert.Null(settings.Username);
            Assert.Null(settings.Password);
            Assert.Null(settings.QueueName);
            Assert.False(settings.PurgeQueueOnStartup);
            Assert.Null(settings.MachineName);
            Assert.Null(settings.ErrorQueueName);
            Assert.False(settings.AuditingEnabled);
            Assert.Null(settings.AuditQueueName);
            Assert.False(settings.DisableErrors);
            Assert.Null(settings.HeartbeatQueueName);
            Assert.Null(settings.ClientSettings);
            Assert.False(settings.SslEnabled);
            Assert.Equal(SslPolicyErrors.None, settings.AcceptablePolicyErrors);
            Assert.Null(settings.ServerName);
            Assert.Null(settings.CertPath);
            Assert.Null(settings.CertPassphrase);
            Assert.Null(settings.Certs);
            Assert.Equal(SslProtocols.None, settings.Version);
            Assert.Null(settings.CertificateSelectionCallback);
            Assert.Null(settings.CertificateValidationCallback);
            Assert.Null(settings.VirtualHost);
        }

        [Fact]
        public void ShouldSetAllProperties()
        {
            // Arrange & Act
            var settings = new TransportSettings
            {
                RetryDelay = 1000,
                MaxRetries = 5,
                Host = "localhost",
                Username = "guest",
                Password = "guest",
                QueueName = "test-queue",
                PurgeQueueOnStartup = true,
                MachineName = "test-machine",
                ErrorQueueName = "error-queue",
                AuditingEnabled = true,
                AuditQueueName = "audit-queue",
                DisableErrors = true,
                HeartbeatQueueName = "heartbeat-queue",
                ClientSettings = new Dictionary<string, object> { { "Key", "Value" } },
                SslEnabled = true,
                AcceptablePolicyErrors = SslPolicyErrors.RemoteCertificateChainErrors,
                ServerName = "server.example.com",
                CertPath = "/path/to/cert",
                CertPassphrase = "password",
                Certs = new X509CertificateCollection(),
                Version = SslProtocols.Tls12,
                VirtualHost = "/test"
            };

            // Assert
            Assert.Equal(1000, settings.RetryDelay);
            Assert.Equal(5, settings.MaxRetries);
            Assert.Equal("localhost", settings.Host);
            Assert.Equal("guest", settings.Username);
            Assert.Equal("guest", settings.Password);
            Assert.Equal("test-queue", settings.QueueName);
            Assert.True(settings.PurgeQueueOnStartup);
            Assert.Equal("test-machine", settings.MachineName);
            Assert.Equal("error-queue", settings.ErrorQueueName);
            Assert.True(settings.AuditingEnabled);
            Assert.Equal("audit-queue", settings.AuditQueueName);
            Assert.True(settings.DisableErrors);
            Assert.Equal("heartbeat-queue", settings.HeartbeatQueueName);
            Assert.Equal("Value", settings.ClientSettings["Key"]);
            Assert.True(settings.SslEnabled);
            Assert.Equal(SslPolicyErrors.RemoteCertificateChainErrors, settings.AcceptablePolicyErrors);
            Assert.Equal("server.example.com", settings.ServerName);
            Assert.Equal("/path/to/cert", settings.CertPath);
            Assert.Equal("password", settings.CertPassphrase);
            Assert.NotNull(settings.Certs);
            Assert.Equal(SslProtocols.Tls12, settings.Version);
            Assert.Equal("/test", settings.VirtualHost);
        }

        [Fact]
        public void ClientSettingsShouldBeNullable()
        {
            // Arrange
            var settings = new TransportSettings();

            // Assert
            Assert.Null(settings.ClientSettings);
        }

        [Fact]
        public void CertsShouldBeNullable()
        {
            // Arrange
            var settings = new TransportSettings();

            // Assert
            Assert.Null(settings.Certs);
        }
    }
}