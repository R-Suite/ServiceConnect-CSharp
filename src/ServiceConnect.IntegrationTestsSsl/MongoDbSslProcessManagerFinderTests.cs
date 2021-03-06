﻿//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

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
                "xxx",
                "xxx",
                "xxx",
                "xxx");

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
