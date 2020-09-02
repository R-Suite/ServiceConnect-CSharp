using System;

namespace ServiceConnect.Interfaces
{
    public interface ILogger
    {
        void Debug(string message);
        void Info(string message);
        void Error(string message, Exception ex = null);
        void Warn(string message, Exception ex = null);
        void Fatal(string message, Exception ex = null);
    }
}