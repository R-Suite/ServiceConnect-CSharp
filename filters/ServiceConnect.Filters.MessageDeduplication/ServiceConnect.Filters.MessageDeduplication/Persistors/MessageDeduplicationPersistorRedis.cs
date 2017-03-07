using System;
using Common.Logging;
using StackExchange.Redis;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors
{
    public class RedisConnectionFactory
    {
        private static readonly Lazy<ConnectionMultiplexer> Connection;
        private static readonly DeduplicationFilterSettings Settings = DeduplicationFilterSettings.Instance;

        static RedisConnectionFactory()
        {
            var connectionString = Settings.ConnectionStringRedis;
            var options = ConfigurationOptions.Parse(connectionString);

            Connection = new Lazy<ConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(options));
        }

        public static ConnectionMultiplexer GetConnection() => Connection.Value;

    }

    public class MessageDeduplicationPersistorRedis : IMessageDeduplicationPersistor
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(MessageDeduplicationPersistorMongoDb));
        private static readonly DeduplicationFilterSettings Settings = DeduplicationFilterSettings.Instance;

        public bool GetMessageExists(Guid messageId)
        {
            var con = RedisConnectionFactory.GetConnection();
            var db = con.GetDatabase(Settings.DatabaseIndexRedis);

            return db.KeyExists(messageId.ToString());
        }

        public void Insert(Guid messageId, DateTime messagExpiry)
        {
            var con = RedisConnectionFactory.GetConnection();
            var db = con.GetDatabase(Settings.DatabaseIndexRedis);

            db.StringSet(messageId.ToString(), string.Empty);

            if (!Settings.DisableMsgExpiry)
                db.KeyExpire(messageId.ToString(), messagExpiry);
        }

        public void RemoveExpiredMessages(DateTime messagExpiry)
        {
            // Do nothing, Redis takes case of message expiry.
        }
    }
}
