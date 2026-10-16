using System;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistance.MongoDb;
using Xunit;

namespace ServiceConnect.IntegrationTests
{
    public class AggregatedMessage : Message
    {
        public AggregatedMessage(Guid correlationId) : base(correlationId) { }

        public string Text { get; set; }
    }

    public class MongoDbAggregatorPersistorTests
    {
        private const string DbName = "ScTestAggregatorRepository";
        private const string CollectionName = "Aggregator";
        private readonly string _connectionString = MongoDbFactAttribute.ConnectionString;
        private readonly string _name = typeof(AggregatedMessage).AssemblyQualifiedName;
        private readonly IMongoDatabase _testRepo;

        public MongoDbAggregatorPersistorTests()
        {
            if (string.IsNullOrEmpty(_connectionString))
            {
                return;
            }

            _testRepo = MongoDbConnection.GetDatabase(_connectionString, DbName);
            _testRepo.DropCollection(CollectionName);
        }

        [MongoDbFact]
        public void ShouldInsertCountAndGetData()
        {
            // Arrange
            var persistor = new MongoDBAggregatorPersistor(_connectionString, DbName, CollectionName);
            var first = new AggregatedMessage(Guid.NewGuid()) { Text = "first" };
            var second = new AggregatedMessage(Guid.NewGuid()) { Text = "second" };

            // Act
            persistor.InsertData(first, _name);
            persistor.InsertData(second, _name);
            persistor.InsertData(new AggregatedMessage(Guid.NewGuid()), "other");

            // Assert
            Assert.Equal(2, persistor.Count(_name));
            var data = persistor.GetData(_name).Cast<AggregatedMessage>().OrderBy(m => m.Text).ToList();
            Assert.Equal(new[] { "first", "second" }, data.Select(m => m.Text));
            Assert.Equal(first.CorrelationId, data[0].CorrelationId);
        }

        [MongoDbFact]
        public void ShouldRemoveDataByCorrelationId()
        {
            // Arrange
            var persistor = new MongoDBAggregatorPersistor(_connectionString, DbName, CollectionName);
            var keep = new AggregatedMessage(Guid.NewGuid()) { Text = "keep" };
            var remove = new AggregatedMessage(Guid.NewGuid()) { Text = "remove" };
            persistor.InsertData(keep, _name);
            persistor.InsertData(remove, _name);

            // Act
            persistor.RemoveData(_name, remove.CorrelationId);

            // Assert
            Assert.Equal(1, persistor.Count(_name));
            Assert.Equal("keep", ((AggregatedMessage)persistor.GetData(_name).Single()).Text);
        }

        [MongoDbFact]
        public void ShouldReadAndRemoveDataWrittenByVersion7()
        {
            // Arrange: a document as ServiceConnect.Persistance.MongoDb 7.0.0 (mongocsharpdriver 2.19) wrote it.
            var persistor = new MongoDBAggregatorPersistor(_connectionString, DbName, CollectionName);
            _testRepo.GetCollection<BsonDocument>(CollectionName).InsertOne(BsonDocument.Parse(@"{
                ""_id"": { ""$binary"": { ""base64"": ""cAVOK+tWCk2ghqPsxIGoog=="", ""subType"": ""03"" } },
                ""Version"": 1,
                ""Data"": {
                    ""_t"": ""AggregatedMessage"",
                    ""CorrelationId"": { ""$binary"": { ""base64"": ""W62PD8vZn0ahZXCGdyiVDg=="", ""subType"": ""03"" } },
                    ""Text"": ""agg""
                },
                ""Name"": """ + _name + @"""
            }"));

            // Act
            var data = persistor.GetData(_name);
            persistor.RemoveData(_name, Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));

            // Assert
            var message = Assert.IsType<AggregatedMessage>(Assert.Single(data));
            Assert.Equal("agg", message.Text);
            Assert.Equal(0, persistor.Count(_name));
        }
    }
}
