//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System.Collections.Generic;

namespace R.MessageBus.Interfaces
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
    }
}
