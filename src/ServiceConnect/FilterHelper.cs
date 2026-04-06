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

using System;
using System.Collections.Generic;
using System.Linq;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;

namespace ServiceConnect
{
    public static class FilterHelper
    {
        public static FilterResult ApplyOutgoingFilters(IList<Type> filters, IBusContainer container, IBus bus, byte[] messageBytes, Dictionary<string, string> headers)
        {
            if (filters == null || filters.Count == 0)
            {
                return new FilterResult { Success = true, MessageBytes = messageBytes, Headers = headers };
            }

            var envelope = new Envelope
            {
                Headers = headers == null ? new Dictionary<string, object>() : headers.ToDictionary(x => x.Key, x => (object)x.Value),
                Body = messageBytes
            };

            bool stop = ProcessFilters(container, bus, filters, envelope);
            
            if (stop)
            {
                return new FilterResult { Success = false };
            }

            return new FilterResult
            {
                Success = true,
                MessageBytes = envelope.Body,
                Headers = envelope.Headers.ToDictionary(x => x.Key, x => x.Value?.ToString())
            };
        }

        private static bool ProcessFilters(IBusContainer container, IBus bus, IEnumerable<Type> filters, Envelope envelope)
        {
            if (filters == null)
                return false;

            foreach (Type filterType in filters)
            {
                IFilter filter = (IFilter)container.GetInstance(filterType);
                filter.Bus = bus;

                bool stop = !filter.Process(envelope);
                if (stop)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public class FilterResult
    {
        public bool Success { get; set; }
        public byte[] MessageBytes { get; set; }
        public Dictionary<string, string> Headers { get; set; }
    }
}