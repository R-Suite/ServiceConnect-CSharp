using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class TransportSettings : ITransportSettings
    {
        public int RetryDelay { get; set; }
        public int MaxRetries { get; set; }
        public string Host { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string QueueName { get; set; }
        public bool PurgeQueueOnStartup { get; set; }
        public string MachineName { get; set; }
        public string ErrorQueueName { get; set; }
        public bool AuditingEnabled { get; set; }
        public string AuditQueueName { get; set; }
        public bool DisableErrors { get; set; }
        public string HeartbeatQueueName { get; set; }
        public IDictionary<string, object> ClientSettings { get; set; }
        public bool SslEnabled { get; set; }
        public SslPolicyErrors AcceptablePolicyErrors { get; set; }
        public string ServerName { get; set; }
        public string CertPath { get; set; }
        public string CertPassphrase { get; set; }
        public X509CertificateCollection Certs { get; set; }
        public SslProtocols Version { get; set; }
        public LocalCertificateSelectionCallback CertificateSelectionCallback { get; set; }
        public RemoteCertificateValidationCallback CertificateValidationCallback { get; set; }
        public string VirtualHost { get; set; }
    }
}