using System;
using Common.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Core
{
    public class Logger : ILogger
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(Logger));

        public void Debug(string message)
        {
            Log.Debug(message);
        }

        public void Info(string message)
        {
            Log.Info(message);
        }

        public void Error(string message, Exception ex = null)
        {
            if (ex == null)
            {
                Log.Error(message);
            }
            else
            {
                Log.Error(message, ex);
            }
        }

        public void Warn(string message, Exception ex = null)
        {
            if (ex == null)
            {
                Log.Warn(message);
            }
            else
            {
                Log.Warn(message, ex);
            }
        }

        public void Fatal(string message, Exception ex = null)
        {
            if (ex == null)
            {
                Log.Fatal(message);
            }
            else
            {
                Log.Fatal(message, ex);
            }
        }
    }
}