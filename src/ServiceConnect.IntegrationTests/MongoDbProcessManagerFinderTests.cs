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
using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistance.MongoDb;
using Xunit;

namespace ServiceConnect.IntegrationTests
{
    public class TestData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public string Name { get; set; }
    }

    public class MongoDbProcessManagerFinderTests
    {
        readonly Guid _correlationId = Guid.NewGuid();
        private readonly string _connectionString;
        private readonly string _dbName;
        private readonly IProcessManagerPropertyMapper _mapper;
        private readonly string _testCollectionName = "TestData";
        private readonly IMongoDatabase _testRepo;

        public MongoDbProcessManagerFinderTests()
        {
            _dbName = "ProcessManagerRepository";
            _connectionString = MongoDbFactAttribute.ConnectionString;

            _mapper = new ProcessManagerPropertyMapper();
            _mapper.ConfigureMapping<IProcessManagerData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);

            if (string.IsNullOrEmpty(_connectionString))
            {
                return;
            }

            _testRepo = MongoDbConnection.GetDatabase(_connectionString, _dbName);
            _testRepo.DropCollection(_testCollectionName);
            _testRepo.DropCollection("Timeouts");
        }

        [MongoDbFact]
        public void ShouldInsertData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.InsertData(data);

            // Assert
            var collection = _testRepo.GetCollection<MongoDbData<TestData>>(_testCollectionName);
            var filter = Builders<MongoDbData<TestData>>.Filter.Eq(_ => _.Data.CorrelationId, _correlationId);
            var insertedData = collection.Find(filter).First();
            Assert.Equal("TestData", insertedData.Data.Name);
        }

        [MongoDbFact]
        public void ShouldStoreGuidsAsLegacyBinarySubtype()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.InsertData(data);

            // Assert
            var raw = _testRepo.GetCollection<BsonDocument>(_testCollectionName).Find(new BsonDocument()).Single();
            var storedId = raw["Data"]["CorrelationId"].AsBsonBinaryData;
            Assert.Equal(BsonBinarySubType.UuidLegacy, storedId.SubType);
            Assert.Equal(_correlationId.ToByteArray(), storedId.Bytes);
            Assert.Equal("TestData", raw["Data"]["_t"].AsString);
        }

        [MongoDbFact]
        public void ShouldUpsertData()
        {
            // Arrange
            IProcessManagerData data1 = new TestData { CorrelationId = _correlationId, Name = "TestData1" };
            IProcessManagerData data2 = new TestData { CorrelationId = _correlationId, Name = "TestData2" };
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.InsertData(data1);
            processManagerFinder.InsertData(data2);

            // Assert
            var collection = _testRepo.GetCollection<MongoDbData<TestData>>(_testCollectionName);
            var filter = Builders<MongoDbData<TestData>>.Filter.Eq(_ => _.Data.CorrelationId, _correlationId);
            var insertedData = collection.Find(filter).ToList();
            Assert.Single(insertedData);
            Assert.Equal("TestData2", insertedData[0].Data.Name);
        }

        [MongoDbFact]
        public void ShouldFindData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IMongoCollection<MongoDbData<IProcessManagerData>> collection = _testRepo.GetCollection<MongoDbData<IProcessManagerData>>(_testCollectionName);
            collection.InsertOne(new MongoDbData<IProcessManagerData> { Data = data });
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            // Act
            var result = processManagerFinder.FindData<TestData>(_mapper, new Message(_correlationId));

            // Assert
            Assert.Equal("TestData", result.Data.Name);
        }

        [MongoDbFact]
        public void ShouldReturnNullWhenDataNotFound()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            // Act
            var result = processManagerFinder.FindData<TestData>(_mapper, new Message(_correlationId));

            // Assert
            Assert.Null(result);
        }

        [MongoDbFact]
        public void ShouldUpdateData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            var collection = _testRepo.GetCollection<MongoDbData<IProcessManagerData>>(_testCollectionName);
            var versionData = new MongoDbData<IProcessManagerData> { Data = data };
            collection.InsertOne(versionData);
            ((TestData)data).Name = "TestDataUpdated";
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.UpdateData(versionData);

            // Assert
            var collection2 = _testRepo.GetCollection<MongoDbData<TestData>>(_testCollectionName);
            var filter = Builders<MongoDbData<TestData>>.Filter.Eq(_ => _.Data.CorrelationId, _correlationId);
            var updatedData = collection2.Find(filter).First();
            Assert.Equal("TestDataUpdated", updatedData.Data.Name);
            Assert.Equal(1, updatedData.Version);
        }

        [MongoDbFact]
        public void ShouldThrowWhenUpdatingTwoInstancesOfSameDataAtTheSameTime()
        {
            // Arrange
            IProcessManagerData data1 = new TestData { CorrelationId = _correlationId, Name = "TestData1" };
            var collection = _testRepo.GetCollection<MongoDbData<IProcessManagerData>>(_testCollectionName);
            collection.InsertOne(new MongoDbData<IProcessManagerData> { Data = data1 });
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            var foundData1 = processManagerFinder.FindData<TestData>(_mapper, new Message(_correlationId));
            var foundData2 = processManagerFinder.FindData<TestData>(_mapper, new Message(_correlationId));

            processManagerFinder.UpdateData(foundData1); // first update should be fine

            // Act / Assert
            Assert.Throws<ArgumentException>(() => processManagerFinder.UpdateData(foundData2)); // second update should fail
        }

        [MongoDbFact]
        public void ShouldDeleteData()
        {
            // Arrange
            var collection = _testRepo.GetCollection<MongoDbData<IProcessManagerData>>(_testCollectionName);
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            collection.InsertOne(new MongoDbData<IProcessManagerData> { Data = data });
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.DeleteData(new MongoDbData<IProcessManagerData> { Data = data });

            // Assert
            var collection2 = _testRepo.GetCollection<MongoDbData<TestData>>(_testCollectionName);
            var filter = Builders<MongoDbData<TestData>>.Filter.Eq(_ => _.Data.CorrelationId, _correlationId);
            var deletedData = collection2.Find(filter).FirstOrDefault();
            Assert.Null(deletedData);
        }

        [MongoDbFact]
        public void ShouldFindAndUpdateDataWrittenByVersion7()
        {
            // Arrange: a document as ServiceConnect.Persistance.MongoDb 7.0.0 (mongocsharpdriver 2.19) wrote it.
            var correlationId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
            _testRepo.GetCollection<BsonDocument>(_testCollectionName).InsertOne(BsonDocument.Parse(@"{
                ""_id"": { ""$binary"": { ""base64"": ""CD8ln2Jcw0auh6cA8KSlkw=="", ""subType"": ""03"" } },
                ""Version"": 1,
                ""Data"": {
                    ""_t"": ""TestData"",
                    ""CorrelationId"": { ""$binary"": { ""base64"": ""W62PD8vZn0ahZXCGdyiVDg=="", ""subType"": ""03"" } },
                    ""Name"": ""Written by 7.0.0""
                },
                ""Name"": null
            }"));
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            // Act
            var found = processManagerFinder.FindData<TestData>(_mapper, new Message(correlationId));
            found.Data.Name = "Updated";
            processManagerFinder.UpdateData(found);
            var updated = processManagerFinder.FindData<TestData>(_mapper, new Message(correlationId));
            processManagerFinder.DeleteData(updated);

            // Assert
            Assert.Equal(correlationId, found.Data.CorrelationId);
            Assert.Equal("Updated", updated.Data.Name);
            Assert.Equal(2, ((MongoDbData<TestData>)updated).Version);
            Assert.Equal(0, _testRepo.GetCollection<BsonDocument>(_testCollectionName).CountDocuments(new BsonDocument()));
        }

        [MongoDbFact]
        public void ShouldInsertTimeoutAndRaiseEvent()
        {
            // Arrange
            var processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);
            var time = DateTime.UtcNow.AddMinutes(5);
            DateTime? raised = null;
            processManagerFinder.TimeoutInserted += t => raised = t;

            // Act
            processManagerFinder.InsertTimeout(new TimeoutData { Id = Guid.NewGuid(), ProcessManagerId = _correlationId, Destination = "dest", Time = time });

            // Assert
            Assert.Equal(time, raised);
            Assert.Equal(1, _testRepo.GetCollection<TimeoutData>("Timeouts").CountDocuments(Builders<TimeoutData>.Filter.Empty));
        }

        [MongoDbFact]
        public void ShouldLockAndReturnOnlyDueTimeouts()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);
            var dueId = Guid.NewGuid();
            var futureTime = DateTime.UtcNow.AddMinutes(10);
            processManagerFinder.InsertTimeout(new TimeoutData { Id = dueId, ProcessManagerId = _correlationId, Destination = "dest", Time = DateTime.UtcNow.AddMinutes(-1) });
            processManagerFinder.InsertTimeout(new TimeoutData { Id = Guid.NewGuid(), ProcessManagerId = _correlationId, Destination = "dest", Time = futureTime });

            // Act
            var firstBatch = processManagerFinder.GetTimeoutsBatch();
            var secondBatch = processManagerFinder.GetTimeoutsBatch();

            // Assert
            var due = Assert.Single(firstBatch.DueTimeouts);
            Assert.Equal(dueId, due.Id);
            Assert.True(due.Locked);
            Assert.Equal(futureTime.Ticks / TimeSpan.TicksPerMillisecond, firstBatch.NextQueryTime.Ticks / TimeSpan.TicksPerMillisecond);
            Assert.Empty(secondBatch.DueTimeouts);
        }

        [MongoDbFact]
        public void ShouldRemoveOnlyTheDispatchedTimeout()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            processManagerFinder.InsertTimeout(new TimeoutData { Id = firstId, ProcessManagerId = _correlationId, Destination = "dest", Time = DateTime.UtcNow.AddMinutes(-2) });
            processManagerFinder.InsertTimeout(new TimeoutData { Id = secondId, ProcessManagerId = _correlationId, Destination = "dest", Time = DateTime.UtcNow.AddMinutes(-1) });
            Assert.Equal(2, processManagerFinder.GetTimeoutsBatch().DueTimeouts.Count);

            // Act
            processManagerFinder.RemoveDispatchedTimeout(firstId);

            // Assert
            var remaining = _testRepo.GetCollection<TimeoutData>("Timeouts").Find(Builders<TimeoutData>.Filter.Empty).ToList();
            Assert.Equal(secondId, Assert.Single(remaining).Id);
        }

        [MongoDbFact]
        public void ShouldReadTimeoutWrittenByVersion7()
        {
            // Arrange: an unlocked, due timeout as ServiceConnect.Persistance.MongoDb 7.0.0 wrote it.
            _testRepo.GetCollection<BsonDocument>("Timeouts").InsertOne(BsonDocument.Parse(@"{
                ""_id"": { ""$binary"": { ""base64"": ""eWaefCV03kCUS+B/wfkK5w=="", ""subType"": ""03"" } },
                ""Destination"": ""dest"",
                ""ProcessManagerId"": { ""$binary"": { ""base64"": ""W62PD8vZn0ahZXCGdyiVDg=="", ""subType"": ""03"" } },
                ""Time"": { ""$date"": { ""$numberLong"": ""1577836800000"" } },
                ""Headers"": null,
                ""Locked"": false
            }"));
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);

            // Act
            var batch = processManagerFinder.GetTimeoutsBatch();
            processManagerFinder.RemoveDispatchedTimeout(batch.DueTimeouts.Single().Id);

            // Assert
            var timeout = Assert.Single(batch.DueTimeouts);
            Assert.Equal(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"), timeout.Id);
            Assert.Equal(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), timeout.ProcessManagerId);
            Assert.Equal(0, _testRepo.GetCollection<BsonDocument>("Timeouts").CountDocuments(new BsonDocument()));
        }

        [MongoDbFact]
        public void ShouldStoreTimeoutIdsAsLegacyBinarySubtype()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);
            var id = Guid.NewGuid();

            // Act
            processManagerFinder.InsertTimeout(new TimeoutData { Id = id, ProcessManagerId = _correlationId, Destination = "dest", Time = DateTime.UtcNow });

            // Assert
            var raw = _testRepo.GetCollection<BsonDocument>("Timeouts").Find(new BsonDocument()).Single();
            Assert.Equal(BsonBinarySubType.UuidLegacy, raw["_id"].AsBsonBinaryData.SubType);
            Assert.Equal(id.ToByteArray(), raw["_id"].AsBsonBinaryData.Bytes);
            Assert.Equal(BsonBinarySubType.UuidLegacy, raw["ProcessManagerId"].AsBsonBinaryData.SubType);
        }

        [MongoDbFact]
        public void ShouldUseEarliestUpcomingTimeoutAsNextQueryTime()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);
            var earliest = DateTime.UtcNow.AddMinutes(5);
            processManagerFinder.InsertTimeout(new TimeoutData { Id = Guid.NewGuid(), ProcessManagerId = _correlationId, Destination = "dest", Time = earliest.AddMinutes(20) });
            processManagerFinder.InsertTimeout(new TimeoutData { Id = Guid.NewGuid(), ProcessManagerId = _correlationId, Destination = "dest", Time = earliest });
            processManagerFinder.InsertTimeout(new TimeoutData { Id = Guid.NewGuid(), ProcessManagerId = _correlationId, Destination = "dest", Time = earliest.AddMinutes(10) });

            // Act
            var batch = processManagerFinder.GetTimeoutsBatch();

            // Assert
            Assert.Empty(batch.DueTimeouts);
            Assert.Equal(earliest.Ticks / TimeSpan.TicksPerMillisecond, batch.NextQueryTime.Ticks / TimeSpan.TicksPerMillisecond);
        }

        [MongoDbFact]
        public void ShouldQueryAgainInAMinuteWhenNoTimeoutIsUpcoming()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);
            var before = DateTime.UtcNow;

            // Act
            var batch = processManagerFinder.GetTimeoutsBatch();

            // Assert
            Assert.InRange(batch.NextQueryTime, before.AddMinutes(1), DateTime.UtcNow.AddMinutes(1));
        }

        [MongoDbFact]
        public void ShouldUpdateTheDocumentThatWasFoundWhenDuplicatesExist()
        {
            // Arrange: earlier versions inserted a new document on every InsertData call.
            var collection = _testRepo.GetCollection<MongoDbData<IProcessManagerData>>(_testCollectionName);
            var secondId = Guid.NewGuid();
            collection.InsertOne(new MongoDbData<IProcessManagerData> { Id = Guid.NewGuid(), Version = 1, Data = new TestData { CorrelationId = _correlationId, Name = "first" } });
            collection.InsertOne(new MongoDbData<IProcessManagerData> { Id = secondId, Version = 1, Data = new TestData { CorrelationId = _correlationId, Name = "second" } });
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);
            var found = _testRepo.GetCollection<MongoDbData<TestData>>(_testCollectionName).Find(Builders<MongoDbData<TestData>>.Filter.Eq(_ => _.Id, secondId)).Single();
            found.Data.Name = "updated";

            // Act
            processManagerFinder.UpdateData(found);

            // Assert
            var stored = _testRepo.GetCollection<MongoDbData<TestData>>(_testCollectionName).Find(Builders<MongoDbData<TestData>>.Filter.Eq(_ => _.Id, found.Id)).Single();
            Assert.Equal("updated", stored.Data.Name);
            Assert.Equal(2, stored.Version);
        }

        [MongoDbFact]
        public void ShouldDeleteEveryCopyOfTheData()
        {
            // Arrange: earlier versions could store more than one document for a CorrelationId.
            var collection = _testRepo.GetCollection<MongoDbData<IProcessManagerData>>(_testCollectionName);
            collection.InsertOne(new MongoDbData<IProcessManagerData> { Id = Guid.NewGuid(), Version = 1, Data = new TestData { CorrelationId = _correlationId, Name = "first" } });
            collection.InsertOne(new MongoDbData<IProcessManagerData> { Id = Guid.NewGuid(), Version = 1, Data = new TestData { CorrelationId = _correlationId, Name = "second" } });
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);
            var found = processManagerFinder.FindData<TestData>(_mapper, new Message(_correlationId));

            // Act
            processManagerFinder.DeleteData(found);

            // Assert
            Assert.Null(processManagerFinder.FindData<TestData>(_mapper, new Message(_correlationId)));
        }

        [MongoDbFact]
        public void ShouldSkipATimeoutItCannotReadAndDispatchTheOthers()
        {
            // Arrange: a timeout stored with a Guid representation other than the registered one.
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);
            var first = Guid.NewGuid();
            var last = Guid.NewGuid();
            var unreadable = Guid.NewGuid();
            processManagerFinder.InsertTimeout(new TimeoutData { Id = first, ProcessManagerId = _correlationId, Destination = "dest", Time = DateTime.UtcNow.AddMinutes(-3) });
            var timeouts = _testRepo.GetCollection<BsonDocument>("Timeouts");
            timeouts.InsertOne(new BsonDocument
            {
                { "_id", new BsonBinaryData(unreadable, GuidRepresentation.Standard) },
                { "Destination", "dest" },
                { "ProcessManagerId", new BsonBinaryData(_correlationId, GuidRepresentation.Standard) },
                { "Time", DateTime.UtcNow.AddMinutes(-2) },
                { "Headers", BsonNull.Value },
                { "Locked", false }
            });
            processManagerFinder.InsertTimeout(new TimeoutData { Id = last, ProcessManagerId = _correlationId, Destination = "dest", Time = DateTime.UtcNow.AddMinutes(-1) });

            // Act
            var firstBatch = processManagerFinder.GetTimeoutsBatch();
            var secondBatch = processManagerFinder.GetTimeoutsBatch();

            // Assert
            Assert.Equal(new[] { first, last }, firstBatch.DueTimeouts.Select(t => t.Id).OrderBy(id => Array.IndexOf(new[] { first, last }, id)));
            Assert.Empty(secondBatch.DueTimeouts);
            var stored = timeouts.Find(Builders<BsonDocument>.Filter.Eq("_id", new BsonBinaryData(unreadable, GuidRepresentation.Standard))).Single();
            Assert.False(stored["Locked"].AsBoolean);
        }

        [MongoDbFact]
        public void ShouldUseTheNextTimeoutEvenWhenItCannotBeRead()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);
            var next = DateTime.UtcNow.AddMinutes(5);
            _testRepo.GetCollection<BsonDocument>("Timeouts").InsertOne(new BsonDocument
            {
                { "_id", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard) },
                { "Destination", "dest" },
                { "ProcessManagerId", new BsonBinaryData(_correlationId, GuidRepresentation.Standard) },
                { "Time", next },
                { "Headers", BsonNull.Value },
                { "Locked", false }
            });

            // Act
            var batch = processManagerFinder.GetTimeoutsBatch();

            // Assert
            Assert.Equal(next.Ticks / TimeSpan.TicksPerMillisecond, batch.NextQueryTime.Ticks / TimeSpan.TicksPerMillisecond);
        }

        [MongoDbFact]
        public void ShouldReturnDueTimeoutsWhenTheNextTimeoutHasATimeOutOfRange()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbProcessManagerFinder(_connectionString, _dbName);
            var due = Guid.NewGuid();
            processManagerFinder.InsertTimeout(new TimeoutData { Id = due, ProcessManagerId = _correlationId, Destination = "dest", Time = DateTime.UtcNow.AddMinutes(-1) });
            _testRepo.GetCollection<BsonDocument>("Timeouts").InsertOne(new BsonDocument
            {
                { "_id", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.CSharpLegacy) },
                { "Destination", "dest" },
                { "ProcessManagerId", new BsonBinaryData(_correlationId, GuidRepresentation.CSharpLegacy) },
                { "Time", new BsonDateTime(25_000_000_000_000_000L) },
                { "Headers", BsonNull.Value },
                { "Locked", false }
            });
            var before = DateTime.UtcNow;

            // Act
            var batch = processManagerFinder.GetTimeoutsBatch();

            // Assert
            Assert.Equal(due, Assert.Single(batch.DueTimeouts).Id);
            Assert.InRange(batch.NextQueryTime, before.AddMinutes(1), DateTime.UtcNow.AddMinutes(1));
        }
    }
}
