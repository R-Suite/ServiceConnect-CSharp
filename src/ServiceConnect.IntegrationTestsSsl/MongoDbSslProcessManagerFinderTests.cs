//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using MongoDB.Driver;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistance.MongoDbSsl;
using Xunit;

namespace ServiceConnect.IntegrationTestsSsl
{
    public class TestDataSsl : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public string Name { get; set; }
    }

    public class MongoDbSslProcessManagerFinderTests
    {
        readonly Guid _correlationId = Guid.NewGuid();
        private readonly string _connectionString;
        private readonly string _dbName;
        private readonly IProcessManagerPropertyMapper _mapper;
        private readonly string _testCollectionName = "TestDataSsl";

        public MongoDbSslProcessManagerFinderTests()
        {
            _dbName = "ScTestProcessManagerRepository";
            _connectionString = string.Format("nodes={0},username={1},password={2},cert={3}",
                "londattst01;londattst02;londattst03",
                "mongo_admin",
                "j8rjWucfUuK3UXg8",
                "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");

            _mapper = new ProcessManagerPropertyMapper();
            _mapper.ConfigureMapping<IProcessManagerData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);

            var testRepo = new MongoDbSslRepository(_connectionString, _dbName);
            testRepo.MongoDatabase.DropCollection("TestDataSsl");
        }

        [Fact]
        public void ShouldInsertData()
        {
            // Arrange
            IProcessManagerData data = new TestDataSsl { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.InsertData(data);

            // Assert
            var testRepo = new MongoDbSslRepository(_connectionString, _dbName);
            var collection = testRepo.MongoDatabase.GetCollection<MongoDbSslData<TestDataSsl>>(_testCollectionName);
            var filter = Builders<MongoDbSslData<TestDataSsl>>.Filter.Eq(_ => _.Data.CorrelationId, _correlationId);
            var insertedData = collection.Find(filter).First();
            Assert.Equal("TestData", insertedData.Data.Name);
        }

        [Fact]
        public void ShouldUpsertData()
        {
            // Arrange
            IProcessManagerData data1 = new TestDataSsl { CorrelationId = _correlationId, Name = "TestData1" };
            IProcessManagerData data2 = new TestDataSsl { CorrelationId = _correlationId, Name = "TestData2" };
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.InsertData(data1);
            processManagerFinder.InsertData(data2);

            // Assert
            var testRepo = new MongoDbSslRepository(_connectionString, _dbName);
            var collection = testRepo.MongoDatabase.GetCollection<MongoDbSslData<TestDataSsl>>(_testCollectionName);
            var filter = Builders<MongoDbSslData<TestDataSsl>>.Filter.Eq(_ => _.Data.CorrelationId, _correlationId);
            MongoDbSslData<TestDataSsl> insertedData = collection.Find(filter).First();
            Assert.Equal("TestData2", insertedData.Data.Name);
        }

        [Fact]
        public void ShouldFindData()
        {
            // Arrange
            var testRepo = new MongoDbSslRepository(_connectionString, _dbName);
            IProcessManagerData data = new TestDataSsl { CorrelationId = _correlationId, Name = "TestData" };
            IMongoCollection<MongoDbSslData<IProcessManagerData>> collection = testRepo.MongoDatabase.GetCollection<MongoDbSslData<IProcessManagerData>>(_testCollectionName);
            collection.InsertOne(new MongoDbSslData<IProcessManagerData> { Data = data });
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            // Act
            var result = processManagerFinder.FindData<TestDataSsl>(_mapper, new Message(_correlationId));

            // Assert
            Assert.Equal("TestData", result.Data.Name);
        }

        [Fact]
        public void ShouldReturnNullWhenDataNotFound()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            // Act
            var result = processManagerFinder.FindData<TestDataSsl>(_mapper, new Message(_correlationId));

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void ShouldUpdateData()
        {
            // Arrange
            var testRepo = new MongoDbSslRepository(_connectionString, _dbName);
            IProcessManagerData data = new TestDataSsl { CorrelationId = _correlationId, Name = "TestData" };
            var collection = testRepo.MongoDatabase.GetCollection<MongoDbSslData<IProcessManagerData>>(_testCollectionName);
            var versionData = new MongoDbSslData<IProcessManagerData> { Data = data };
            collection.InsertOne(versionData);
            ((TestDataSsl)data).Name = "TestDataUpdated";
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.UpdateData(versionData);

            // Assert
            var collection2 = testRepo.MongoDatabase.GetCollection<MongoDbSslData<TestDataSsl>>(_testCollectionName);
            var filter = Builders<MongoDbSslData<TestDataSsl>>.Filter.Eq(_ => _.Data.CorrelationId, _correlationId);
            var updatedData = collection2.Find(filter).First();
            Assert.Equal("TestDataUpdated", updatedData.Data.Name);
            Assert.Equal(1, updatedData.Version);
        }

        [Fact]
        public void ShouldThrowWhenUpdatingTwoInstancesOfSameDataAtTheSameTime()
        {
            // Arrange
            var testRepo = new MongoDbSslRepository(_connectionString, _dbName);
            IProcessManagerData data1 = new TestDataSsl { CorrelationId = _correlationId, Name = "TestData1" };
            var collection = testRepo.MongoDatabase.GetCollection<MongoDbSslData<IProcessManagerData>>(_testCollectionName);
            collection.InsertOne(new MongoDbSslData<IProcessManagerData> { Data = data1 });
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            var foundData1 = processManagerFinder.FindData<TestDataSsl>(_mapper, new Message(_correlationId));
            var foundData2 = processManagerFinder.FindData<TestDataSsl>(_mapper, new Message(_correlationId));

            processManagerFinder.UpdateData(foundData1); // first update should be fine

            // Act / Assert
            Assert.Throws<ArgumentException>(() => processManagerFinder.UpdateData(foundData2)); // second update should fail
        }

        [Fact]
        public void ShouldDeleteData()
        {
            // Arrange
            var testRepo = new MongoDbSslRepository(_connectionString, _dbName);
            var collection = testRepo.MongoDatabase.GetCollection<MongoDbSslData<IProcessManagerData>>(_testCollectionName);
            IProcessManagerData data = new TestDataSsl { CorrelationId = _correlationId, Name = "TestData" };
            collection.InsertOne(new MongoDbSslData<IProcessManagerData> { Data = data });
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.DeleteData(new MongoDbSslData<IProcessManagerData> { Data = data });

            // Assert
            var collection2 = testRepo.MongoDatabase.GetCollection<MongoDbSslData<TestDataSsl>>(_testCollectionName);
            var filter = Builders<MongoDbSslData<TestDataSsl>>.Filter.Eq(_ => _.Data.CorrelationId, _correlationId);
            var deletedData = collection2.Find(filter).FirstOrDefault();
            Assert.Null(deletedData);
        }
    }
}
