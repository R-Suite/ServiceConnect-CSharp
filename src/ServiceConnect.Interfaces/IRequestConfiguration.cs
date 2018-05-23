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

using System;
using System.Threading.Tasks;

namespace ServiceConnect.Interfaces
{
    public interface IRequestConfiguration
    {
        /// <summary>
        /// Keeps track of the original request message
        /// Check this property when processing reply messages to ensure the request is not proccessed as a reply. 
        /// </summary>
        Guid RequestMessageId { get; }

        int EndpointsCount { get; set; }
        int ProcessedCount { get; set; }

        /// <summary>
        /// Configures a handler.
        /// </summary>
        /// <param name="handler">The handler to call with the response message</param>
        Task SetHandler(Action<object> handler);

        void ProcessMessage(string message, Type typeObject);
    }
}
