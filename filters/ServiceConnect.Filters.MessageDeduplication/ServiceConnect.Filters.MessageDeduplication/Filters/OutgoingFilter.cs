using System;
using System.Text;
using Common.Logging;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;
using Timer = System.Threading.Timer;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    /// <summary>
    /// Provides uniform implementation of the (outgoing filter) message deduplication
    /// for all the different types of filters with various persistence mechanisms.
    /// </summary>
    public class OutgoingFilter
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(OutgoingFilter));
        private readonly IMessageDeduplicationPersistor _messageDeduplicationPersistor;
        private static Timer _timer;
        private readonly DeduplicationFilterSettings _settings;
        private static readonly object Padlock = new object();

        public Timer Timer { get { return _timer;} }

        /// <summary>
        /// Public ctor
        /// </summary>
        /// <param name="messageDeduplicationPersistor"></param>
        public OutgoingFilter(IMessageDeduplicationPersistor messageDeduplicationPersistor)
        {
            // get instance of the global settings object
            _settings = DeduplicationFilterSettings.Instance;

            // setup persistance store
            if (_messageDeduplicationPersistor == null)
            {
                _messageDeduplicationPersistor = messageDeduplicationPersistor;
            }

            // setup timer for cleaning up expired messages
            lock (Padlock)
            {
                // note: no need to timer with Redis persistor
                if (_timer == null && !_settings.DisableMsgExpiry && messageDeduplicationPersistor.GetType() != typeof(MessageDeduplicationPersistorRedis))
                {
                    _timer = new Timer(Callback, null, 0, _settings.MsgCleanupIntervalMinutes * 60 * 1000);
                }
            }
        }

        /// <summary>
        /// Executes on every specified time interval and removes the expired message ids from the relevant persistance store.
        /// </summary>
        /// <param name="state"></param>
        private void Callback(object state)
        {
            try
            {
                _messageDeduplicationPersistor.RemoveExpiredMessages(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                Logger.Error("Error removing expired messages.", ex);
            }
        }

        public bool Process(Envelope envelope)
        {
            try
            {
                _messageDeduplicationPersistor.Insert(
                    new Guid(Encoding.UTF8.GetString((byte[]) envelope.Headers["MessageId"])),
                    DateTime.UtcNow.AddHours(_settings.MsgExpiryHours));
            }
            catch (Exception ex)
            {
                Logger.ErrorFormat("envelope: {0}", Newtonsoft.Json.JsonConvert.SerializeObject(envelope));
                Logger.Error("Error processing outgoing deduplication filter ", ex);
            }

            return true;
        }
    }
}
