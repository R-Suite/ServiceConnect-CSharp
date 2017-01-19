//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,git 
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;

namespace ServiceConnect.Interfaces
{
    public interface ITransportSettings
    {
        /// <summary>
        /// Delay (in miliseconds) between bus attempts to redeliver message
        /// </summary>
        int RetryDelay { get; set; }

        /// <summary>
        /// Maximum number of retries
        /// </summary>
        int MaxRetries { get; set; }

        /// <summary>
        /// Messaging host
        /// </summary>
        string Host { get; set; }

        /// <summary>
        /// Messaging host username
        /// </summary>
        string Username { get; set; }

        /// <summary>
        /// Messaging host password
        /// </summary>
        string Password { get; set; }

        string MachineName { get; set; }

        /// <summary>
        /// Custom Error Queue Name
        /// </summary>
        string ErrorQueueName { get; set; }

        /// <summary>
        /// Auditing enabled
        /// </summary>
        bool AuditingEnabled { get; set; }

        /// <summary>
        /// Custom Audit Queue Name
        /// </summary>
        string AuditQueueName { get; set; }

        /// <summary>
        /// Disable sending errors to error queue
        /// </summary>
        bool DisableErrors { get; set; }

        /// <summary>
        /// Custom Heartbeat Queue Name
        /// </summary>
        string HeartbeatQueueName { get; set; }

        /// <summary>
        /// Contains settings specific to client
        /// </summary>
        IDictionary<string, object> ClientSettings { get; set; }

        string QueueName { get; set; }

        bool PurgeQueueOnStartup { get; set; }

        /// <summary>
        /// Communicate over AMQPS instead of AMQP?
        /// See also <seealso cref="AcceptablePolicyErrors" />, <seealso cref="ServerName" />, <seealso cref="CertPath"/>,
        /// <seealso cref="CertPassphrase"/>, <seealso cref="Certs"/>, <seealso cref="Version"/>,
        /// <seealso cref="CertificateSelectionCallback"/>, <seealso cref="CertificateValidationCallback"/>
        /// for configuring SSL specific aspects of transport
        /// </summary>
        bool SslEnabled { get; set; }

        /// <summary>
        /// Used during server certificate validation. Useful mainly for development purposes.
        /// In production, this should be left to <seealso cref="SslPolicyErrors.None" /> (default)
        /// </summary>
        SslPolicyErrors AcceptablePolicyErrors { get; set; }

        /// <summary>
        /// Used during SSL validation.
        /// If set, it must match exactly with Canonical Name (CN) of the certificate.
        /// Useful for wildcard certificates where rabbitmq factory will fail to validate ssl certificate otherwise.
        /// </summary>
        string ServerName { get; set; }

        /// <summary>
        /// Optional client certificate to use during SSL handshake
        /// </summary>
        string CertPath { get; set; }

        /// <summary>
        /// Password for the optional client certificate used during SSL handshake
        /// </summary>
        string CertPassphrase { get; set; }

        /// <summary>
        /// X509CertificateCollection containing the optional client certificate.
        /// If no collection is set, the client will attempt to load one from the specified <see cref="CertPath"/>
        /// </summary>
        X509CertificateCollection Certs { get; set; }

        /// <summary>
        /// Optionally use specific ssl protocol version
        /// </summary>
        SslProtocols Version { get; set; }

        /// <summary>
        /// An optional client specified SSL certificate selection callback.  If this is not specified,
        /// the first valid certificate found will be used.
        /// </summary>
        LocalCertificateSelectionCallback CertificateSelectionCallback { get; set; }

        /// <summary>
        /// An optional client specified SSL certificate validation callback.  If this is not specified,
        /// the default callback will be used in conjunction with the <seealso cref="AcceptablePolicyErrors"/> property to
        /// determine if the remote server certificate is valid.
        /// </summary>
        RemoteCertificateValidationCallback CertificateValidationCallback { get; set; }

        /// <summary>
        /// Virtual host to be used for communication.
        /// This should only be set if your setup actually has this configured.
        /// This value is case sensitive. Incorrectly changing this value will break all communications with RabbitMQ server.
        /// </summary>
        string VirtualHost { get; set; }
    }
}
