using System;
using System.Collections.Generic;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class MessageBusWriteStream : IMessageBusWriteStream
    {
        private IProducer _producer;
        private readonly long _packetSize;
        private readonly string _endPoint;
        private readonly string _sequenceId;
        private Int64 _packetsSent;

        public MessageBusWriteStream(IProducer producer, string endPoint, string sequenceId)
        {
            _producer = producer;
            _endPoint = endPoint;
            _sequenceId = sequenceId;
            _packetSize = producer.MaximumMessageSize;
            _packetsSent = 0;
        }

        public void Write(byte[] data)
        {
            for (Int64 i = 0; i < data.Length; i++)
            {
                var subArray = SubArray(data, i, _packetSize);
                i = i + (_packetSize - 1);
                _packetsSent++;
                _producer.SendBytes(_endPoint, subArray, new Dictionary<string, string>
                {
                    { "SequenceId", _sequenceId },
                    { "PacketNumber", _packetsSent.ToString() }
                });
            }
        }

        private static byte[] SubArray(byte[] data, Int64 index, Int64 length)
        {
            if (data.Length < index + length)
            {
                length = data.Length - index;
            }
            byte[] result = new byte[length];
            Array.Copy(data, index, result, 0, length);
            return result;
        }

        public void Close()
        {
            _packetsSent++;
            _producer.SendBytes(_endPoint, new byte[0], new Dictionary<string, string>
            {
                { "SequenceId", _sequenceId },
                { "Stop", string.Empty },
                { "PacketNumber", _packetsSent.ToString()}
            });
        }
        
        public void Dispose()
        {
            Close();
            _producer = null;
        }
    }
}