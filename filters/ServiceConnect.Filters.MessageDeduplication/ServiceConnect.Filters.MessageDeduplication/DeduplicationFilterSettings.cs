using System;

namespace ServiceConnect.Filters.MessageDeduplication
{
    /// <summary>
    /// Global settings object implemented as singleton
    /// </summary>
    public sealed class DeduplicationFilterSettings
    {
        public int MsgExpiryHours { get; set; }

        /// <summary>
        /// How often to clean up expired messages from the persistance store
        /// </summary>
        public int MsgCleanupIntervalMinutes { get; set; }
        /// <summary>
        /// Redis persistance store connection string
        /// </summary>
        public string ConnectionStringRedis { get; set; }

        /// <summary>
        /// Database index (0-15)
        /// </summary>
        public int DatabaseIndexRedis { get; set; }

        /// <summary>
        /// MongoDb(Ssl) persistance store connection string
        /// </summary>
        public string ConnectionStringMongoDb { get; set; }

        /// <summary>
        /// Name of the MongoDb database
        /// </summary>
        public string DatabaseNameMongoDb { get; set; }

        /// <summary>
        /// Name fo the MongoDb collection
        /// </summary>
        public string CollectionNameMongoDb { get; set; }

        /// <summary>
        /// Disable message expiry.
        /// Processed messages in the persistance store won't get deleted.
        /// </summary>
        public bool DisableMsgExpiry { get; set; }

        /// <summary>
        /// Allocate ourselves.
        /// We have a private constructor, so no one else can.
        /// </summary>
        //static readonly DeduplicationFilterSettings _instance = new DeduplicationFilterSettings();


        private static readonly Lazy<DeduplicationFilterSettings> Lazy = new Lazy<DeduplicationFilterSettings>(() => new DeduplicationFilterSettings());

        /// <summary>
        /// Access DeduplicationFilterSettings.Instance to get the singleton object.
        /// Then call methods on that instance.
        /// </summary>
        //public static DeduplicationFilterSettings Instance
        //{
        //    get { return _instance; }
        //}

        public static DeduplicationFilterSettings Instance { get { return Lazy.Value; } }

        /// <summary>
        /// This is a private constructor, meaning no outsiders have access.
        /// </summary>
        private DeduplicationFilterSettings()
        {
            DisableMsgExpiry = false;
            MsgExpiryHours = 24;
            MsgCleanupIntervalMinutes = 60;
            ConnectionStringMongoDb = "mongodb://localhost";
            DatabaseNameMongoDb = "ServiceConnect-Filters-MessageDeduplication";
            CollectionNameMongoDb = "ProcessedMessages";
            ConnectionStringRedis = "localhost";
            DatabaseIndexRedis = 0;
        }
    }
}
