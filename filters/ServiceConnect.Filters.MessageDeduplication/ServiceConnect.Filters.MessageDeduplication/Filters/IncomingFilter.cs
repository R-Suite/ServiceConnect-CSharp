using System;
using Common.Logging;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    /// <summary>
    /// Provides uniform implementation of the (incoming filter) message deduplication
    /// for all the different types of filters with various persistence mechanisms.
    /// </summary>
    public class IncomingFilter
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(IncomingFilter));
        private readonly IMessageDeduplicationPersistor _messageDeduplicationPersistor;

        /// <summary>
        /// Public ctor
        /// </summary>
        /// <param name="messageDeduplicationPersistor"></param>
        public IncomingFilter(IMessageDeduplicationPersistor messageDeduplicationPersistor)
        {
            // setup persistance store
            _messageDeduplicationPersistor = messageDeduplicationPersistor;
        }

        public bool Process(Envelope envelope)
        {
            bool processMessage = true;

            // Rethrow any possible exception, message will be retried
            try
            {
                /*  
                 * https://www.rabbitmq.com/reliability.html
                 * "...if the redelivered flag is not set then it is guaranteed that the message has not been seen before..."  
                 */
                if (envelope.Headers.ContainsKey("Redelivered") && bool.TryParse(HeaderDecoder.Decode(envelope.Headers["Redelivered"]), out var redelivered) && redelivered)
                {
                    // if exists in persistant storage
                    bool msgAlreadyProcessed =
                        _messageDeduplicationPersistor.GetMessageExists(
                            new Guid(HeaderDecoder.Decode(envelope.Headers["MessageId"]) ?? string.Empty));
                    if (msgAlreadyProcessed)
                    {
                        processMessage = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Fatal("Error checking for duplicate messages.", ex);
                throw;
            }
            
            return processMessage;
        }
    }
}
