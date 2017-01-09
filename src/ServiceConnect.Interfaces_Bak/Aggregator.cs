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

namespace ServiceConnect.Interfaces
{
    /// <summary>
    /// Define aggregated message handlers
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public abstract class Aggregator<T> where T : Message
    {
        /// <summary>
        /// Timeout for aggregating messages.
        /// When the timeout is reached, the current batch of messages is dispatched 
        /// to the handler (regardless of the batch size).
        /// </summary>
        /// <returns></returns>
        public virtual TimeSpan Timeout()
        {
            return default(TimeSpan);
        }

        /// <summary>
        /// Max batch size of aggregated messages
        /// </summary>
        /// <returns></returns>
        public virtual int BatchSize()
        {
            return 0;
        }

        public abstract void Execute(IList<T> messages);
    }
}