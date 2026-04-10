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
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.IntegrationTests
{
    public class TestData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// Minimal IProcessManagerPropertyMapper implementation for integration tests.
    /// Replaces the old ProcessManagerPropertyMapper from ServiceConnect.Core.
    /// </summary>
    public class TestProcessManagerPropertyMapper : IProcessManagerPropertyMapper
    {
        public List<ProcessManagerToMessageMap> Mappings { get; set; } = new();

        public void ConfigureMapping<TProcessManagerData, TMessage>(
            Expression<Func<TProcessManagerData, object>> processManagerProperty,
            Expression<Func<TMessage, object>> messageExpression)
            where TProcessManagerData : IProcessManagerData
        {
            var map = new ProcessManagerToMessageMap
            {
                MessageType = typeof(TMessage),
                PropertiesHierarchy = new Dictionary<string, Type>(),
                MessageProp = BuildMessageFunc(messageExpression)
            };

            var body = processManagerProperty.Body;
            if (body is UnaryExpression unary)
                body = unary.Operand;

            if (body is MemberExpression member)
            {
                var propInfo = (PropertyInfo)member.Member;
                map.PropertiesHierarchy[propInfo.Name] = propInfo.PropertyType;
            }

            Mappings.Add(map);
        }

        private static Func<object, object> BuildMessageFunc<TMessage>(
            Expression<Func<TMessage, object>> messageExpression)
        {
            var compiled = messageExpression.Compile();
            return obj => compiled((TMessage)obj);
        }
    }

    public class MongoDbProcessManagerFinderTests
    {
        private readonly Guid _correlationId = Guid.NewGuid();
        private readonly IMongoCollection<MongoDbData<TestData>> _collection;
        private readonly MongoDbPersistenceOptions _options;
        private readonly IProcessManagerPropertyMapper _mapper;

        public MongoDbProcessManagerFinderTests()
        {
            _options = new MongoDbPersistenceOptions
            {
                ConnectionString = "mongodb://localhost/",
                DatabaseName = "ProcessManagerRepository"
            };

            var mongoClient = new MongoClient(_options.ConnectionString);
            var mongoDatabase = mongoClient.GetDatabase(_options.DatabaseName);
            _collection = mongoDatabase.GetCollection<MongoDbData<TestData>>("TestData");
            _collection.DeleteMany(Builders<MongoDbData<TestData>>.Filter.Empty);

            _mapper = new TestProcessManagerPropertyMapper();
            _mapper.ConfigureMapping<IProcessManagerData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);
        }

        private MongoDbProcessManagerFinder CreateFinder()
        {
            return new MongoDbProcessManagerFinder(_options, NullLogger<MongoDbProcessManagerFinder>.Instance);
        }

        [Fact]
        public void ShouldInsertData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = CreateFinder();

            // Act
            processManagerFinder.InsertData(data);

            // Assert
            var insertedData = _collection.Find(x => x.Data.CorrelationId == _correlationId).FirstOrDefault();
            Assert.NotNull(insertedData);
            Assert.Equal("TestData", insertedData.Data.Name);
        }

        [Fact]
        public void ShouldFindData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            _collection.InsertOne(new MongoDbData<TestData> { Data = (TestData)data, Version = 1 });
            IProcessManagerFinder processManagerFinder = CreateFinder();

            // Act
            var result = processManagerFinder.FindData<TestData>(_mapper, new Message(_correlationId));

            // Assert
            Assert.NotNull(result);
            Assert.Equal("TestData", result.Data.Name);
        }

        [Fact]
        public void ShouldReturnNullWhenDataNotFound()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = CreateFinder();

            // Act
            var result = processManagerFinder.FindData<TestData>(_mapper, new Message(_correlationId));

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void ShouldUpdateData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            _collection.InsertOne(new MongoDbData<TestData> { Data = (TestData)data, Version = 1 });
            IProcessManagerFinder processManagerFinder = CreateFinder();
            var versionData = (MongoDbData<TestData>)processManagerFinder.FindData<TestData>(_mapper, new Message(_correlationId))!;
            versionData.Data.Name = "TestDataUpdated";

            // Act
            processManagerFinder.UpdateData(versionData);

            // Assert
            var updatedData = _collection.Find(x => x.Data.CorrelationId == _correlationId).FirstOrDefault();
            Assert.NotNull(updatedData);
            Assert.Equal("TestDataUpdated", updatedData.Data.Name);
            Assert.Equal(2, updatedData.Version);
        }

        [Fact]
        public void ShouldThrowWhenUpdatingTwoInstancesOfSameDataAtTheSameTime()
        {
            // Arrange
            IProcessManagerData data1 = new TestData { CorrelationId = _correlationId, Name = "TestData1" };
            _collection.InsertOne(new MongoDbData<TestData> { Data = (TestData)data1, Version = 1 });
            IProcessManagerFinder processManagerFinder = CreateFinder();

            var foundData1 = processManagerFinder.FindData<TestData>(_mapper, new Message(_correlationId));
            var foundData2 = processManagerFinder.FindData<TestData>(_mapper, new Message(_correlationId));

            processManagerFinder.UpdateData(foundData1!); // first update should be fine

            // Act / Assert
            Assert.Throws<ArgumentException>(() => processManagerFinder.UpdateData(foundData2!)); // second update should fail
        }

        [Fact]
        public void ShouldDeleteData()
        {
            // Arrange
            var data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            var mongoData = new MongoDbData<TestData> { Data = data, Version = 1 };
            _collection.InsertOne(mongoData);
            IProcessManagerFinder processManagerFinder = CreateFinder();

            // Act
            processManagerFinder.DeleteData(mongoData);

            // Assert
            var deletedData = _collection.Find(x => x.Data.CorrelationId == _correlationId).FirstOrDefault();
            Assert.Null(deletedData);
        }
    }
}
