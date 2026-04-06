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
using System.Collections.Generic;
using System.Text;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Core
{
    public class ConsumeContext : IConsumeContext
    {
        private IBus _bus;

        public IBus Bus
        {
            set { _bus = value; }
        }

        public IDictionary<string, object> Headers { get; set; }

        public void Reply<TReply>(TReply message, Dictionary<string, string> headers) where TReply : Message
        {
            if (Headers == null || !Headers.ContainsKey("RequestMessageId"))
            {
                throw new ArgumentException("RequestMessageId not found in message headers.");
            }

            var requestMessageId = Headers["RequestMessageId"] as byte[];
            if (requestMessageId == null)
            {
                throw new ArgumentException("RequestMessageId is not a byte array.");
            }

            headers["ResponseMessageId"] = Encoding.ASCII.GetString(requestMessageId);

            if (_bus == null)
            {
                throw new InvalidOperationException("Bus is not set on ConsumeContext.");
            }

            if (Headers.ContainsKey("SourceAddress"))
            {
                var sourceAddress = Headers["SourceAddress"] as byte[];
                if (sourceAddress != null)
                {
                    _bus.Send(Encoding.ASCII.GetString(sourceAddress), message, headers);
                }
            }
            else
            {
                throw new ArgumentException("SourceAddress not found in message headers.");
            }
        }

        public void Reply<TReply>(TReply message) where TReply : Message
        {
            Reply(message, new Dictionary<string, string>());
        }
    }
}
