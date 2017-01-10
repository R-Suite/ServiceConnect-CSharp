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
using ServiceConnect.Interfaces;

namespace ServiceConnect.Core
{
    public class MessageBusReadStream : IMessageBusReadStream
    {
        private long _currentPacket = 1;
        private readonly SortedDictionary<Int64, byte[]> _packetQueue = new SortedDictionary<Int64, byte[]>();
        private readonly object _byteStreamLock = new object();

        public Int64 LastPacketNumber { get; set; }
        public MessageBusStreamComplete CompleteEventHandler { get; set; }
        public string SequenceId { get; set; }

        public int HandlerCount { get; set; }

        public bool IsComplete()
        {
            var complete = LastPacketNumber == _currentPacket;
            if (complete)
            {
                CompleteEventHandler(SequenceId);
            }
            return complete;
        }

        public void Write(byte[] data, Int64 packetNumber)
        {
            lock (_byteStreamLock)
            {
                _packetQueue.Add(packetNumber, data);
            }
        }

        public byte[] Read()
        {
            lock (_byteStreamLock)
            {
                if (!_packetQueue.ContainsKey(_currentPacket))
                {
                    return new byte[0];
                }

                var data = _packetQueue[_currentPacket];
                _packetQueue.Remove(_currentPacket);
                _currentPacket++;
                return data;
            }
        }
    }
}