using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MongoDB.Driver.Builders;
using R.MessageBus.Interfaces;
using R.MessageBus.Persistance.MongoDb;
using R.MessageBus.Persistance.SqlServer;
using Xunit;

namespace R.MessageBus.IntegrationTests
{
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
            _connectionString = "Server=localhost;Database=ProcessManagerRepository;Trusted_Connection=True;";
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
            //todo
        }
    }
}
