using System;
using System.Linq;
using MongoDB.Bson;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistance.MongoDbSsl;
using Xunit;

namespace ServiceConnect.IntegrationTestsSsl
{
    public class AggregatedMessageSsl : Message
    {
        public AggregatedMessageSsl(Guid correlationId) : base(correlationId) { }

        public string Text { get; set; }
    }

    public class MongoDbSslAggregatorPersistorTests
    {
        private const string DbName = "ScTestAggregatorRepository";
        private const string CollectionName = "Aggregator";
        private readonly string _connectionString = MongoDbSslFactAttribute.ConnectionString;
        private readonly string _name = typeof(AggregatedMessageSsl).AssemblyQualifiedName;
        private readonly MongoDbSslRepository _testRepo;

        public MongoDbSslAggregatorPersistorTests()
        {
            if (string.IsNullOrEmpty(_connectionString))
            {
                return;
            }

            _testRepo = new MongoDbSslRepository(_connectionString, DbName);
            _testRepo.MongoDatabase.DropCollection(CollectionName);
        }

        [MongoDbSslFact]
        public void ShouldInsertCountAndGetData()
        {
            // Arrange
            var persistor = new MongoDBSslAggregatorPersistor(_connectionString, DbName, CollectionName);
            var first = new AggregatedMessageSsl(Guid.NewGuid()) { Text = "first" };
            var second = new AggregatedMessageSsl(Guid.NewGuid()) { Text = "second" };

            // Act
            persistor.InsertData(first, _name);
            persistor.InsertData(second, _name);
            persistor.InsertData(new AggregatedMessageSsl(Guid.NewGuid()), "other");

            // Assert
            Assert.Equal(2, persistor.Count(_name));
            var data = persistor.GetData(_name).Cast<AggregatedMessageSsl>().OrderBy(m => m.Text).ToList();
            Assert.Equal(new[] { "first", "second" }, data.Select(m => m.Text));
            Assert.Equal(first.CorrelationId, data[0].CorrelationId);
        }

        [MongoDbSslFact]
        public void ShouldRemoveDataByCorrelationId()
        {
            // Arrange
            var persistor = new MongoDBSslAggregatorPersistor(_connectionString, DbName, CollectionName);
            var keep = new AggregatedMessageSsl(Guid.NewGuid()) { Text = "keep" };
            var remove = new AggregatedMessageSsl(Guid.NewGuid()) { Text = "remove" };
            persistor.InsertData(keep, _name);
            persistor.InsertData(remove, _name);

            // Act
            persistor.RemoveData(_name, remove.CorrelationId);

            // Assert
            Assert.Equal(1, persistor.Count(_name));
            Assert.Equal("keep", ((AggregatedMessageSsl)persistor.GetData(_name).Single()).Text);
        }

        [MongoDbSslFact]
        public void ShouldReadAndRemoveDataWrittenByVersion7()
        {
            // Arrange: a document as ServiceConnect.Persistance.MongoDbSsl 7.0.0 (MongoDB.Driver 2.19) wrote it.
            var persistor = new MongoDBSslAggregatorPersistor(_connectionString, DbName, CollectionName);
            _testRepo.MongoDatabase.GetCollection<BsonDocument>(CollectionName).InsertOne(BsonDocument.Parse(@"{
                ""_id"": { ""$oid"": ""6ad1ee6abff3d8415041c057"" },
                ""Version"": 1,
                ""Data"": {
                    ""_t"": ""AggregatedMessageSsl"",
                    ""CorrelationId"": { ""$binary"": { ""base64"": ""W62PD8vZn0ahZXCGdyiVDg=="", ""subType"": ""03"" } },
                    ""Text"": ""agg""
                },
                ""Name"": """ + _name + @""",
                ""Locked"": false
            }"));

            // Act
            var data = persistor.GetData(_name);
            persistor.RemoveData(_name, Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));

            // Assert
            var message = Assert.IsType<AggregatedMessageSsl>(Assert.Single(data));
            Assert.Equal("agg", message.Text);
            Assert.Equal(0, persistor.Count(_name));
        }
    }
}
