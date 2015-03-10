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
using R.MessageBus.Interfaces;

namespace R.MessageBus.Settings
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
    }
}
