using System;
using System.Collections.Generic;
using System.IO;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
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