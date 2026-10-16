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
using System.IO;
using System.Linq;
using MongoDB.Bson;
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
        private readonly MongoDbSslRepository _testRepo;

        public MongoDbSslProcessManagerFinderTests()
        {
            _dbName = "ScTestProcessManagerRepository";
            _connectionString = MongoDbSslFactAttribute.ConnectionString;

            _mapper = new ProcessManagerPropertyMapper();
            _mapper.ConfigureMapping<IProcessManagerData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);

            if (string.IsNullOrEmpty(_connectionString))
            {
                return;
            }

            _testRepo = new MongoDbSslRepository(_connectionString, _dbName);
            _testRepo.MongoDatabase.DropCollection(_testCollectionName);
            _testRepo.MongoDatabase.DropCollection("Timeouts");
        }

        [MongoDbSslFact]
        public void ShouldInsertData()
        {
            // Arrange
            IProcessManagerData data = new TestDataSsl { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.InsertData(data);

            // Assert
            var collection = _testRepo.MongoDatabase.GetCollection<MongoDbSslData<TestDataSsl>>(_testCollectionName);
            var filter = Builders<MongoDbSslData<TestDataSsl>>.Filter.Eq(_ => _.Data.CorrelationId, _correlationId);
            var insertedData = collection.Find(filter).First();
            Assert.Equal("TestData", insertedData.Data.Name);
        }

        [MongoDbSslFact]
        public void ShouldStoreGuidsAsLegacyBinarySubtype()
        {
            // Arrange
            IProcessManagerData data = new TestDataSsl { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.InsertData(data);

            // Assert
            var raw = _testRepo.MongoDatabase.GetCollection<BsonDocument>(_testCollectionName).Find(new BsonDocument()).Single();
            var storedId = raw["Data"]["CorrelationId"].AsBsonBinaryData;
            Assert.Equal(BsonBinarySubType.UuidLegacy, storedId.SubType);
            Assert.Equal(_correlationId.ToByteArray(), storedId.Bytes);
            Assert.Equal("TestDataSsl", raw["Data"]["_t"].AsString);
        }

        [MongoDbSslFact]
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
            var collection = _testRepo.MongoDatabase.GetCollection<MongoDbSslData<TestDataSsl>>(_testCollectionName);
            var filter = Builders<MongoDbSslData<TestDataSsl>>.Filter.Eq(_ => _.Data.CorrelationId, _correlationId);
            var insertedData = collection.Find(filter).ToList();
            Assert.Single(insertedData);
            Assert.Equal("TestData2", insertedData[0].Data.Name);
        }

        [MongoDbSslFact]
        public void ShouldFindData()
        {
            // Arrange
            IProcessManagerData data = new TestDataSsl { CorrelationId = _correlationId, Name = "TestData" };
            IMongoCollection<MongoDbSslData<IProcessManagerData>> collection = _testRepo.MongoDatabase.GetCollection<MongoDbSslData<IProcessManagerData>>(_testCollectionName);
            collection.InsertOne(new MongoDbSslData<IProcessManagerData> { Data = data });
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            // Act
            var result = processManagerFinder.FindData<TestDataSsl>(_mapper, new Message(_correlationId));

            // Assert
            Assert.Equal("TestData", result.Data.Name);
        }

        [MongoDbSslFact]
        public void ShouldReturnNullWhenDataNotFound()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            // Act
            var result = processManagerFinder.FindData<TestDataSsl>(_mapper, new Message(_correlationId));

            // Assert
            Assert.Null(result);
        }

        [MongoDbSslFact]
        public void ShouldUpdateData()
        {
            // Arrange
            IProcessManagerData data = new TestDataSsl { CorrelationId = _correlationId, Name = "TestData" };
            var collection = _testRepo.MongoDatabase.GetCollection<MongoDbSslData<IProcessManagerData>>(_testCollectionName);
            var versionData = new MongoDbSslData<IProcessManagerData> { Data = data };
            collection.InsertOne(versionData);
            ((TestDataSsl)data).Name = "TestDataUpdated";
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.UpdateData(versionData);

            // Assert
            var collection2 = _testRepo.MongoDatabase.GetCollection<MongoDbSslData<TestDataSsl>>(_testCollectionName);
            var filter = Builders<MongoDbSslData<TestDataSsl>>.Filter.Eq(_ => _.Data.CorrelationId, _correlationId);
            var updatedData = collection2.Find(filter).First();
            Assert.Equal("TestDataUpdated", updatedData.Data.Name);
            Assert.Equal(1, updatedData.Version);
        }

        [MongoDbSslFact]
        public void ShouldThrowWhenUpdatingTwoInstancesOfSameDataAtTheSameTime()
        {
            // Arrange
            IProcessManagerData data1 = new TestDataSsl { CorrelationId = _correlationId, Name = "TestData1" };
            var collection = _testRepo.MongoDatabase.GetCollection<MongoDbSslData<IProcessManagerData>>(_testCollectionName);
            collection.InsertOne(new MongoDbSslData<IProcessManagerData> { Data = data1 });
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            var foundData1 = processManagerFinder.FindData<TestDataSsl>(_mapper, new Message(_correlationId));
            var foundData2 = processManagerFinder.FindData<TestDataSsl>(_mapper, new Message(_correlationId));

            processManagerFinder.UpdateData(foundData1); // first update should be fine

            // Act / Assert
            Assert.Throws<ArgumentException>(() => processManagerFinder.UpdateData(foundData2)); // second update should fail
        }

        [MongoDbSslFact]
        public void ShouldDeleteData()
        {
            // Arrange
            var collection = _testRepo.MongoDatabase.GetCollection<MongoDbSslData<IProcessManagerData>>(_testCollectionName);
            IProcessManagerData data = new TestDataSsl { CorrelationId = _correlationId, Name = "TestData" };
            collection.InsertOne(new MongoDbSslData<IProcessManagerData> { Data = data });
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            // Act
            processManagerFinder.DeleteData(new MongoDbSslData<IProcessManagerData> { Data = data });

            // Assert
            var collection2 = _testRepo.MongoDatabase.GetCollection<MongoDbSslData<TestDataSsl>>(_testCollectionName);
            var filter = Builders<MongoDbSslData<TestDataSsl>>.Filter.Eq(_ => _.Data.CorrelationId, _correlationId);
            var deletedData = collection2.Find(filter).FirstOrDefault();
            Assert.Null(deletedData);
        }

        [MongoDbSslFact]
        public void ShouldFindAndUpdateDataWrittenByVersion7()
        {
            // Arrange: a document as ServiceConnect.Persistance.MongoDbSsl 7.0.0 (MongoDB.Driver 2.19) wrote it.
            var correlationId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
            _testRepo.MongoDatabase.GetCollection<BsonDocument>(_testCollectionName).InsertOne(BsonDocument.Parse(@"{
                ""_id"": { ""$oid"": ""6ad1ee6abff3d8415041c04e"" },
                ""Version"": 1,
                ""Data"": {
                    ""_t"": ""TestDataSsl"",
                    ""CorrelationId"": { ""$binary"": { ""base64"": ""W62PD8vZn0ahZXCGdyiVDg=="", ""subType"": ""03"" } },
                    ""Name"": ""Written by 7.0.0""
                },
                ""Name"": null,
                ""Locked"": false
            }"));
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            // Act
            var found = processManagerFinder.FindData<TestDataSsl>(_mapper, new Message(correlationId));
            found.Data.Name = "Updated";
            processManagerFinder.UpdateData(found);
            var updated = processManagerFinder.FindData<TestDataSsl>(_mapper, new Message(correlationId));
            processManagerFinder.DeleteData(updated);

            // Assert
            Assert.Equal(correlationId, found.Data.CorrelationId);
            Assert.Equal("Updated", updated.Data.Name);
            Assert.Equal(2, ((MongoDbSslData<TestDataSsl>)updated).Version);
            Assert.Equal(0, _testRepo.MongoDatabase.GetCollection<BsonDocument>(_testCollectionName).CountDocuments(new BsonDocument()));
        }

        [MongoDbSslFact]
        public void ShouldInsertTimeoutAndRaiseEvent()
        {
            // Arrange
            var processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);
            var time = DateTime.UtcNow.AddMinutes(5);
            DateTime? raised = null;
            processManagerFinder.TimeoutInserted += t => raised = t;

            // Act
            processManagerFinder.InsertTimeout(new TimeoutData { Id = Guid.NewGuid(), ProcessManagerId = _correlationId, Destination = "dest", Time = time });

            // Assert
            Assert.Equal(time, raised);
            Assert.Equal(1, _testRepo.MongoDatabase.GetCollection<TimeoutData>("Timeouts").CountDocuments(Builders<TimeoutData>.Filter.Empty));
        }

        [MongoDbSslFact]
        public void ShouldLockAndReturnOnlyDueTimeouts()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);
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

        [MongoDbSslFact]
        public void ShouldRemoveOnlyTheDispatchedTimeout()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            processManagerFinder.InsertTimeout(new TimeoutData { Id = firstId, ProcessManagerId = _correlationId, Destination = "dest", Time = DateTime.UtcNow.AddMinutes(-2) });
            processManagerFinder.InsertTimeout(new TimeoutData { Id = secondId, ProcessManagerId = _correlationId, Destination = "dest", Time = DateTime.UtcNow.AddMinutes(-1) });
            Assert.Equal(2, processManagerFinder.GetTimeoutsBatch().DueTimeouts.Count);

            // Act
            processManagerFinder.RemoveDispatchedTimeout(firstId);

            // Assert
            var remaining = _testRepo.MongoDatabase.GetCollection<TimeoutData>("Timeouts").Find(Builders<TimeoutData>.Filter.Empty).ToList();
            Assert.Equal(secondId, Assert.Single(remaining).Id);
        }

        [MongoDbSslFact]
        public void ShouldReadTimeoutWrittenByVersion7()
        {
            // Arrange: an unlocked, due timeout as ServiceConnect.Persistance.MongoDbSsl 7.0.0 wrote it.
            _testRepo.MongoDatabase.GetCollection<BsonDocument>("Timeouts").InsertOne(BsonDocument.Parse(@"{
                ""_id"": { ""$binary"": { ""base64"": ""eWaefCV03kCUS+B/wfkK5w=="", ""subType"": ""03"" } },
                ""Destination"": ""dest"",
                ""ProcessManagerId"": { ""$binary"": { ""base64"": ""W62PD8vZn0ahZXCGdyiVDg=="", ""subType"": ""03"" } },
                ""Time"": { ""$date"": { ""$numberLong"": ""1577836800000"" } },
                ""Headers"": null,
                ""Locked"": false
            }"));
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);

            // Act
            var batch = processManagerFinder.GetTimeoutsBatch();
            processManagerFinder.RemoveDispatchedTimeout(batch.DueTimeouts.Single().Id);

            // Assert
            var timeout = Assert.Single(batch.DueTimeouts);
            Assert.Equal(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"), timeout.Id);
            Assert.Equal(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), timeout.ProcessManagerId);
            Assert.Equal(0, _testRepo.MongoDatabase.GetCollection<BsonDocument>("Timeouts").CountDocuments(new BsonDocument()));
        }

        [MongoDbSslFact]
        public void ShouldConnectWithBase64Certificate()
        {
            // Arrange: swap certpath=<file> for cert=<base64 of the same file>.
            var parts = _connectionString.Split(',');
            var certPathPart = parts.FirstOrDefault(p => p.StartsWith("certpath=", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(certPathPart);
            var base64 = Convert.ToBase64String(File.ReadAllBytes(certPathPart.Substring("certpath=".Length)));
            var connectionString = string.Join(",", parts.Select(p => p == certPathPart ? "cert=" + base64 : p));
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(connectionString, _dbName);

            // Act
            processManagerFinder.InsertData(new TestDataSsl { CorrelationId = _correlationId, Name = "TestData" });

            // Assert
            Assert.Equal("TestData", processManagerFinder.FindData<TestDataSsl>(_mapper, new Message(_correlationId)).Data.Name);
        }

        [MongoDbSslFact]
        public void ShouldConnectWithHighIterationCertificate()
        {
            // Arrange: scripts/start-test-mongo.sh exports the client certificate a second time with 400,000 iterations.
            var parts = _connectionString.Split(',');
            var certPathPart = parts.Single(p => p.StartsWith("certpath=", StringComparison.OrdinalIgnoreCase));
            var highIterationPath = Path.Combine(Path.GetDirectoryName(certPathPart.Substring("certpath=".Length)), "client-high-iteration.pfx");
            Assert.True(File.Exists(highIterationPath), highIterationPath + " is missing; rerun scripts/start-test-mongo.sh.");
            var connectionString = string.Join(",", parts.Select(p => p == certPathPart ? "certpath=" + highIterationPath : p));
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(connectionString, _dbName);

            // Act
            processManagerFinder.InsertData(new TestDataSsl { CorrelationId = _correlationId, Name = "TestData" });

            // Assert
            Assert.Equal("TestData", processManagerFinder.FindData<TestDataSsl>(_mapper, new Message(_correlationId)).Data.Name);
        }

        [MongoDbSslFact]
        public void ShouldStoreTimeoutIdsAsLegacyBinarySubtype()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);
            var id = Guid.NewGuid();

            // Act
            processManagerFinder.InsertTimeout(new TimeoutData { Id = id, ProcessManagerId = _correlationId, Destination = "dest", Time = DateTime.UtcNow });

            // Assert
            var raw = _testRepo.MongoDatabase.GetCollection<BsonDocument>("Timeouts").Find(new BsonDocument()).Single();
            Assert.Equal(BsonBinarySubType.UuidLegacy, raw["_id"].AsBsonBinaryData.SubType);
            Assert.Equal(id.ToByteArray(), raw["_id"].AsBsonBinaryData.Bytes);
            Assert.Equal(BsonBinarySubType.UuidLegacy, raw["ProcessManagerId"].AsBsonBinaryData.SubType);
        }

        [MongoDbSslFact]
        public void ShouldUseEarliestUpcomingTimeoutAsNextQueryTime()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);
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

        [MongoDbSslFact]
        public void ShouldQueryAgainInAMinuteWhenNoTimeoutIsUpcoming()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);
            var before = DateTime.UtcNow;

            // Act
            var batch = processManagerFinder.GetTimeoutsBatch();

            // Assert
            Assert.InRange(batch.NextQueryTime, before.AddMinutes(1), DateTime.UtcNow.AddMinutes(1));
        }

        [MongoDbSslFact]
        public void ShouldDeleteEveryCopyOfTheData()
        {
            // Arrange: earlier versions could store more than one document for a CorrelationId.
            var collection = _testRepo.MongoDatabase.GetCollection<MongoDbSslData<IProcessManagerData>>(_testCollectionName);
            collection.InsertOne(new MongoDbSslData<IProcessManagerData> { Version = 1, Data = new TestDataSsl { CorrelationId = _correlationId, Name = "first" } });
            collection.InsertOne(new MongoDbSslData<IProcessManagerData> { Version = 1, Data = new TestDataSsl { CorrelationId = _correlationId, Name = "second" } });
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);
            var found = processManagerFinder.FindData<TestDataSsl>(_mapper, new Message(_correlationId));

            // Act
            processManagerFinder.DeleteData(found);

            // Assert
            Assert.Null(processManagerFinder.FindData<TestDataSsl>(_mapper, new Message(_correlationId)));
        }

        [MongoDbSslFact]
        public void ShouldSkipATimeoutItCannotReadAndDispatchTheOthers()
        {
            // Arrange: a timeout stored with a Guid representation other than the registered one.
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);
            var first = Guid.NewGuid();
            var last = Guid.NewGuid();
            var unreadable = Guid.NewGuid();
            processManagerFinder.InsertTimeout(new TimeoutData { Id = first, ProcessManagerId = _correlationId, Destination = "dest", Time = DateTime.UtcNow.AddMinutes(-3) });
            var timeouts = _testRepo.MongoDatabase.GetCollection<BsonDocument>("Timeouts");
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

        [MongoDbSslFact]
        public void ShouldUseTheNextTimeoutEvenWhenItCannotBeRead()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);
            var next = DateTime.UtcNow.AddMinutes(5);
            _testRepo.MongoDatabase.GetCollection<BsonDocument>("Timeouts").InsertOne(new BsonDocument
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

        [MongoDbSslFact]
        public void ShouldReturnDueTimeoutsWhenTheNextTimeoutHasATimeOutOfRange()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new MongoDbSslProcessManagerFinder(_connectionString, _dbName);
            var due = Guid.NewGuid();
            processManagerFinder.InsertTimeout(new TimeoutData { Id = due, ProcessManagerId = _correlationId, Destination = "dest", Time = DateTime.UtcNow.AddMinutes(-1) });
            _testRepo.MongoDatabase.GetCollection<BsonDocument>("Timeouts").InsertOne(new BsonDocument
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
