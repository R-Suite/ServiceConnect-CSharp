using System;
using System.Reflection;
using System.Text;
using System.Timers;
using Common.Logging;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    /// <summary>
    /// Provides uniform implementation of the (outgoing filter) message deduplication
    /// for all the different types of filters with various persistence mechanisms.
    /// </summary>
    public class OutgoingFilter
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static IMessageDeduplicationPersistor _messageDeduplicationPersistor;
        private static Timer _timer;
        private readonly DeduplicationFilterSettings _settings;
        private static readonly object Padlock = new object();

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
                if (_timer == null && !_settings.DisableMsgExpiry)
                {
                    _timer = new Timer(_settings.MsgCleanupIntervalMinutes*60*1000);
                    _timer.Elapsed += TimerElapsed;
                    _timer.Start();
                }
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
                Logger.Error("Error processing outgoing deduplication filter ", ex);
            }

            return true;
        }

        /// <summary>
        /// Executes on every specified time interval and removes the expired message ids from the relevant persistance store.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        static void TimerElapsed(object sender, ElapsedEventArgs e)
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
    }
}
