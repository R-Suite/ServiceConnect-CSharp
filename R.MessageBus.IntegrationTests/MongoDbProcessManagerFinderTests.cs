using System;
using System.Configuration;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using R.MessageBus.Interfaces;
using R.MessageBus.Persistance.MongoDb;
using Xunit;

namespace R.MessageBus.IntegrationTests
{
    public class TestData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public string Name { get; set; }
    }

    public class MongoDbProcessManagerFinderTests
    {
        readonly Guid _correlationId = Guid.NewGuid();
        private readonly MongoCollection<TestData> _collection;
        private readonly string _connectionString;
        private readonly string _dbName;


        public MongoDbProcessManagerFinderTests()
        {
            _connectionString = ConfigurationManager.AppSettings["MongoDbConnectionString"];
            _dbName = "ProcessManagerRepository";
            var mongoClient = new MongoClient(_connectionString);
            MongoServer server = mongoClient.GetServer();
            MongoDatabase mongoDatabase = server.GetDatabase(_dbName);
            _collection = mongoDatabase.GetCollection<TestData>("TestData");
            _collection.Drop();
        }

        [Fact]
        public void ShouldInsertData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.InsertData(data);

            // Assert
            var insertedData = _collection.FindOneAs<MongoDbData<TestData>>(Query<MongoDbData<TestData>>.Where(i => i.Data.CorrelationId == _correlationId));
            Assert.Equal("TestData", insertedData.Data.Name);
        }

        [Fact]
        public void ShouldFindData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            _collection.Save(new MongoDbData<IProcessManagerData> { Data = data });
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            // Act
            var result = processManagerFinder.FindData<TestData>(_correlationId);

            // Assert
            Assert.Equal("TestData", result.Data.Name);
        }

        [Fact]
        public void ShouldReturnNullWhenDataNotFound()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            // Act
            var result = processManagerFinder.FindData<TestData>(_correlationId);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void ShouldUpdateData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            var versionData = new MongoDbData<IProcessManagerData> { Data = data };
            _collection.Save(versionData);
            ((TestData) data).Name = "TestDataUpdated";
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.UpdateData(versionData);

            // Assert
            var updatedData = _collection.FindOneAs<MongoDbData<TestData>>(Query<MongoDbData<TestData>>.Where(i => i.Data.CorrelationId == _correlationId));
            Assert.Equal("TestDataUpdated", updatedData.Data.Name);
            Assert.Equal(1, updatedData.Version);
        }

        [Fact]
        public void ShouldThrowWhenUpdatingTwoInstancesOfSameDataAtTheSameTime()
        {
            // Arrange
            IProcessManagerData data1 = new TestData { CorrelationId = _correlationId, Name = "TestData1" };
            _collection.Save(new MongoDbData<IProcessManagerData> { Data = data1 }); 
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            var foundData1 = processManagerFinder.FindData<TestData>(_correlationId);
            var foundData2 = processManagerFinder.FindData<TestData>(_correlationId);

            processManagerFinder.UpdateData(foundData1); // first update should be fine

            // Act / Assert
            Assert.Throws<ArgumentException>(() => processManagerFinder.UpdateData(foundData2)); // second update should fail
        }

        [Fact]
        public void ShouldDeleteData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            _collection.Save(new MongoDbData<IProcessManagerData> { Data = data });
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.DeleteData(new MongoDbData<IProcessManagerData> { Data = data });

            // Assert
            var deletedData = _collection.FindOneAs<TestData>(Query<TestData>.Where(i => i.CorrelationId == _correlationId));
            Assert.Null(deletedData);
        }
    }
}
