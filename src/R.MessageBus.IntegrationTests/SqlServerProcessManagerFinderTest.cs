using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using R.MessageBus.Interfaces;
using R.MessageBus.Persistance.SqlServer;
using Xunit;

namespace R.MessageBus.IntegrationTests
{
    public class TestDbRow
    {
        public string Id { get; set; }
        public string DataJson { get; set; }
    }

    public class TestSqlServerData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public string Name { get; set; }
    }

    public class SqlServerProcessManagerFinderTest
    {
        readonly Guid _correlationId = Guid.NewGuid();
        private readonly string _connectionString;

        public SqlServerProcessManagerFinderTest()
        {
            _connectionString = @"Data Source=(LocalDB)\v11.0;AttachDbFilename=|DataDirectory|\MyLocalDb.mdf;Integrated Security=True";
        }

        [Fact]
        public void ShouldInsertData()
        {
            // Arrange
            IProcessManagerData data = new TestSqlServerData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new SqlServerProcessManagerFinder(_connectionString);

            // Act
            processManagerFinder.InsertData(data);

            // Assert
            var results = GetTestDbData();
            Assert.Equal(1, results.Count);
            Assert.Equal(_correlationId.ToString(), results[0].Id);
            Assert.True(results[0].DataJson.Contains("TestData"));
        }

        [Fact]
        public void ShouldUpdateWhenInsertingDataWithExistingId()
        {
            // Arrange
            SetupTestDbData(new List<TestDbRow> { new TestDbRow {Id = _correlationId.ToString(), DataJson = "FakeJsonData" }});

            IProcessManagerData data = new TestSqlServerData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new SqlServerProcessManagerFinder(_connectionString);

            // Act
            processManagerFinder.InsertData(data);

            // Assert
            var results = GetTestDbData();
            Assert.Equal(1, results.Count);
            Assert.Equal(_correlationId.ToString(), results[0].Id);
            Assert.NotEqual("FakeJsonData", results[0].DataJson);
            Assert.True(results[0].DataJson.Contains("TestData"));
        }

        [Fact]
        public void ShouldFindData()
        {
            // Arrange
            var testDataJson = "{\"CorrelationId\":\"e845f0a0-4af0-4d1e-a324-790d49d540ae\",\"Name\":\"TestData\"}";
            SetupTestDbData(new List<TestDbRow> { new TestDbRow { Id = _correlationId.ToString(), DataJson = testDataJson } });
            IProcessManagerFinder processManagerFinder = new SqlServerProcessManagerFinder(_connectionString);

            // Act
            var result = processManagerFinder.FindData<TestSqlServerData>(_correlationId);

            // Assert
            Assert.Equal("TestData", result.Data.Name);
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
                        "CREATE TABLE TestSqlServerData(Id uniqueidentifier NOT NULL, DataJson text NULL);";
                    command.ExecuteNonQuery();
                }

                foreach (var testDbRow in testData)
                {
                    using (var command = new SqlCommand())
                    {
                        command.Connection = connection;
                        command.CommandText = @"INSERT TestSqlServerData (Id, DataJson) VALUES (@Id,@DataJson)";
                        command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = new Guid(testDbRow.Id);
                        command.Parameters.Add("@DataJson", SqlDbType.Text).Value = testDbRow.DataJson;
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        private IList<TestDbRow> GetTestDbData()
        {
            IList<TestDbRow> results = new List<TestDbRow>();

            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand())
                {
                    command.Connection = connection;
                    command.CommandText = string.Format("SELECT * FROM TestSqlServerData WHERE Id = '{0}'", _correlationId.ToString());
                    var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        results.Add(new TestDbRow { Id = reader["Id"].ToString(), DataJson = reader["DataJson"].ToString() });
                    }
                }
            }

            return results;
        }
    }
}
