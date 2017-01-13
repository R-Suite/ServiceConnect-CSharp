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
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using Moq;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistance.SqlServer;
using Xunit;
using Microsoft.Extensions.PlatformAbstractions;

namespace ServiceConnect.IntegrationTests
{
    #region Helper classes

    public class TestDbRow
    {
        public string Id { get; set; }
        public string DataXml { get; set; }
        public int Version { get; set; }
    }

    public class TestSqlServerData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public string Name { get; set; }
    }

    #endregion

    public class SqlServerProcessManagerFinderTest
    {
        private readonly string _connectionString;
        private readonly IProcessManagerPropertyMapper _mapper;

        public SqlServerProcessManagerFinderTest()
        {
            var appBasePath = PlatformServices.Default.Application.ApplicationBasePath;
            //_connectionString = @"Data Source=(LocalDB)\v11.0;AttachDbFilename=|DataDirectory|\MyLocalDb.mdf;Integrated Security=True";
            _connectionString = string.Format(@"Data Source=(LocalDB)\v11.0;AttachDbFilename={0};Integrated Security=True", Path.Combine(appBasePath, "MyLocalDb.mdf"));

            // DROP TABLE before each test
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand())
                {
                    command.Connection = connection;
                    command.CommandText = "IF EXISTS ( SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TestSqlServerData') " +
                        "DROP TABLE TestSqlServerData;";
                    command.ExecuteNonQuery();
                }
            }

            _mapper = new ProcessManagerPropertyMapper();
            _mapper.ConfigureMapping<IProcessManagerData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);
        }

        [Fact]
        public void ShouldInsertData()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            IProcessManagerData data = new TestSqlServerData { CorrelationId = correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new SqlServerProcessManagerFinder(_connectionString, string.Empty);

            // Act
            processManagerFinder.InsertData(data);

            // Assert
            var results = GetTestDbData(correlationId);
            Assert.Equal(1, results.Count);
            Assert.Equal(correlationId.ToString(), results[0].Id);
            Assert.True(results[0].DataXml.Contains("TestData"));
        }

        [Fact]
        public void ShouldUpdateWhenInsertingDataWithExistingId()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            SetupTestDbData(new List<TestDbRow> { new TestDbRow { Id = correlationId.ToString(), DataXml = "FakeJsonData" } });

            IProcessManagerData data = new TestSqlServerData { CorrelationId = correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new SqlServerProcessManagerFinder(_connectionString, string.Empty);

            // Act
            processManagerFinder.InsertData(data);

            // Assert
            var results = GetTestDbData(correlationId);
            Assert.Equal(1, results.Count);
            Assert.Equal(correlationId.ToString(), results[0].Id);
            Assert.NotEqual("FakeJsonData", results[0].DataXml);
            Assert.True(results[0].DataXml.Contains("TestData"));
        }

        [Fact]
        public void ShouldFindData()
        {
            // Arrange
            var correlationId = Guid.NewGuid();

            var data = new TestSqlServerData {CorrelationId = correlationId, Name = "TestData"};
            var xmlSerializer = new XmlSerializer(data.GetType());
            var sww = new StringWriter();
            XmlWriter writer = XmlWriter.Create(sww);
            xmlSerializer.Serialize(writer, data);
            var dataXml = sww.ToString();

            SetupTestDbData(new List<TestDbRow> { new TestDbRow { Id = correlationId.ToString(), DataXml = dataXml, Version = 1} });
            IProcessManagerFinder processManagerFinder = new SqlServerProcessManagerFinder(_connectionString, string.Empty);

            // Act
            var result = processManagerFinder.FindData<TestSqlServerData>(_mapper, new Message(correlationId));

            // Assert
            Assert.Equal("TestData", result.Data.Name);

            // Teardown - complete transaction
            processManagerFinder.UpdateData(result);
        }

        [Fact]
        public void ShouldReturnNullWhenDataTableNotFound()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            IProcessManagerFinder processManagerFinder = new SqlServerProcessManagerFinder(_connectionString, string.Empty);

            // Act
            //var result = processManagerFinder.FindData<TestSqlServerData>(correlationId);
            var result = processManagerFinder.FindData<TestData>(_mapper, new Message(correlationId));

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void ShouldReturnNullWhenDataNotFound()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            SetupTestDbData(null);
            IProcessManagerFinder processManagerFinder = new SqlServerProcessManagerFinder(_connectionString, string.Empty);

            // Act
            var result = processManagerFinder.FindData<TestSqlServerData>(_mapper, new Message(correlationId));

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void ShouldUpdateData()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            var testDataJson = "{\"CorrelationId\":\"e845f0a0-4af0-4d1e-a324-790d49d540ae\",\"Name\":\"TestDataOriginal\"}";

            IProcessManagerData data = new TestSqlServerData { CorrelationId = correlationId, Name = "TestDataOriginal" };
            var xmlSerializer = new XmlSerializer(data.GetType());
            var sww = new StringWriter();
            XmlWriter writer = XmlWriter.Create(sww);
            xmlSerializer.Serialize(writer, data);
            var dataXml = sww.ToString();
            SetupTestDbData(new List<TestDbRow> { new TestDbRow { Id = correlationId.ToString(), DataXml = dataXml } });

            IProcessManagerData updatedData = new TestSqlServerData { CorrelationId = correlationId, Name = "TestDataUpdated" };
            var sqlServerData = new SqlServerData<IProcessManagerData> { Data = updatedData, Id = correlationId };

            IProcessManagerFinder processManagerFinder = new SqlServerProcessManagerFinder(_connectionString, string.Empty);

            // Act
            //processManagerFinder.FindData<TestSqlServerData>(correlationId);
            processManagerFinder.FindData<TestSqlServerData>(_mapper, new Message(correlationId));
            processManagerFinder.UpdateData(sqlServerData);

            // Assert
            var results = GetTestDbData(correlationId);
            Assert.Equal(1, results.Count);
            Assert.Equal(correlationId.ToString(), results[0].Id);
            Assert.False(results[0].DataXml.Contains("TestDataOriginal"));
            Assert.True(results[0].DataXml.Contains("TestDataUpdated"));
        }

        [Fact]
        public void ShouldThrowWhenUpdatingTwoInstancesOfSameDataAtTheSameTime()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            IProcessManagerData data = new TestSqlServerData { CorrelationId = correlationId, Name = "TestDataUpdated" };
            var xmlSerializer = new XmlSerializer(data.GetType());
            var sww = new StringWriter();
            XmlWriter writer = XmlWriter.Create(sww);
            xmlSerializer.Serialize(writer, data);
            var dataXml = sww.ToString();

            SetupTestDbData(new List<TestDbRow> { new TestDbRow { Id = correlationId.ToString(), DataXml = dataXml, Version = 1 } });

            IProcessManagerFinder processManagerFinder = new SqlServerProcessManagerFinder(_connectionString, string.Empty, 1);

            var foundData1 = processManagerFinder.FindData<TestSqlServerData>(_mapper, new Message(correlationId));
            var foundData2 = processManagerFinder.FindData<TestSqlServerData>(_mapper, new Message(correlationId));

            processManagerFinder.UpdateData(foundData1); // first update should be fine

            // Act / Assert
            Assert.Throws<ArgumentException>(() => processManagerFinder.UpdateData(foundData2)); // second update should fail
        }

        [Fact]
        public void ShouldDeleteData()
        {
            // Arrange
            var correlationId = Guid.NewGuid();

            IProcessManagerData data = new TestSqlServerData { CorrelationId = correlationId, Name = "TestDataUpdated" };
            var xmlSerializer = new XmlSerializer(data.GetType());
            var sww = new StringWriter();
            XmlWriter writer = XmlWriter.Create(sww);
            xmlSerializer.Serialize(writer, data);
            var dataXml = sww.ToString();

            SetupTestDbData(new List<TestDbRow> { new TestDbRow { Id = correlationId.ToString(), DataXml = dataXml, Version = 1 } });

            IProcessManagerFinder processManagerFinder = new SqlServerProcessManagerFinder(_connectionString, string.Empty);

            var sqlServerDataToBeDeleted = new SqlServerData<IProcessManagerData> { Data = data, Id = correlationId, Version = 1};

            // Act
            processManagerFinder.FindData<TestData>(_mapper, new Message(correlationId));
            processManagerFinder.DeleteData(sqlServerDataToBeDeleted);

            // Assert
            var results = GetTestDbData(correlationId);
            Assert.Equal(0, results.Count);
        }

        private void SetupTestDbData(IEnumerable<TestDbRow> testData)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();

                // Create table if doesn't exist
                using (var command = new SqlCommand())
                {
                    command.Connection = connection;
                    command.CommandText =
                        "IF NOT EXISTS( SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TestSqlServerData') " +
                        "CREATE TABLE TestSqlServerData(Id uniqueidentifier NOT NULL, DataXml xml NULL, Version int NOT NULL);";
                    command.ExecuteNonQuery();
                }

                if (null != testData)
                {
                    foreach (var testDbRow in testData)
                    {
                        using (var command = new SqlCommand())
                        {
                            command.Connection = connection;
                            command.CommandText = @"INSERT TestSqlServerData (Id, DataXml, Version) VALUES (@Id,@DataXml,@Version)";
                            command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = new Guid(testDbRow.Id);
                            command.Parameters.Add("@DataXml", SqlDbType.Xml).Value = testDbRow.DataXml;
                            command.Parameters.Add("@Version", SqlDbType.Int).Value = testDbRow.Version;
                            command.ExecuteNonQuery();
                        }
                    }
                }
            }
        }

        private IList<TestDbRow> GetTestDbData(Guid correlationId)
        {
            IList<TestDbRow> results = new List<TestDbRow>();

            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand())
                {
                    command.Connection = connection;
                    command.CommandText = string.Format("SELECT * FROM TestSqlServerData WHERE Id = '{0}'", correlationId);
                    var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        results.Add(new TestDbRow { Id = reader["Id"].ToString(), DataXml = reader["DataXml"].ToString() });
                    }
                }
            }

            return results;
        }
    }
}
