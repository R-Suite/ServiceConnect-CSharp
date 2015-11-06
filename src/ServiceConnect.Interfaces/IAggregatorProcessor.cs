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

namespace ServiceConnect.Interfaces
{
    /// <summary>
    /// Aggregate messages into batches of a predefined size
    /// and pass them to relevant handlers
    /// </summary>
    public interface IAggregatorProcessor : IDisposable
    {
        void ProcessMessage<T>(string message) where T : Message;
        void StartTimer<T>(TimeSpan timeout);
        void ResetTimer();
    }
}