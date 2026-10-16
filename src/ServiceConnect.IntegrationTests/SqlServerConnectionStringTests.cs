using Microsoft.Data.SqlClient;
using ServiceConnect.Persistance.SqlServer;
using Xunit;

namespace ServiceConnect.IntegrationTests
{
    public class SqlServerConnectionStringTests
    {
        [Theory]
        [InlineData("Server=db;User ID=sa;Password=p")]
        [InlineData("Server=db;User ID=sa;Password=p;Encrypt=False")]
        [InlineData("Server=db;User ID=sa;Password=p;TrustServerCertificate=False")]
        [InlineData("Server=db;User ID=sa;Password=p;Encrypt=False;TrustServerCertificate=False")]
        public void ShouldConnectAsSystemDataSqlClientDidWhenEncryptIsNotTrue(string connectionString)
        {
            var result = new SqlConnectionStringBuilder(SqlServerProcessManagerFinder.WithUnencryptedDefault(connectionString));

            Assert.Equal(SqlConnectionEncryptOption.Optional, result.Encrypt);
            Assert.True(result.TrustServerCertificate);
            Assert.Equal("db", result.DataSource);
            Assert.Equal("p", result.Password);
        }

        [Theory]
        [InlineData("Server=db;Encrypt=True")]
        [InlineData("Server=db;Encrypt=Strict")]
        [InlineData("Server=db;Encrypt=True;TrustServerCertificate=True")]
        public void ShouldKeepAConnectionStringThatAsksForEncryption(string connectionString)
        {
            Assert.Equal(connectionString, SqlServerProcessManagerFinder.WithUnencryptedDefault(connectionString));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not a connection string")]
        [InlineData("Server=db;Connect Timeout=abc")]
        [InlineData("Server=db;Network Library=dbmssocn")]
        public void ShouldLeaveAMissingOrMalformedConnectionStringAlone(string connectionString)
        {
            Assert.Equal(connectionString, SqlServerProcessManagerFinder.WithUnencryptedDefault(connectionString));
        }
    }
}
