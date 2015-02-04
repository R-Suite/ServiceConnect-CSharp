using System;

namespace R.MessageBus.Interfaces
{
    public interface IMessageBusWriteStream : IDisposable
    {
        void Write(byte[] data);
        void Close();
    }
}