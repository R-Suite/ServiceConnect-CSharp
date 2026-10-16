using Microsoft.Data.SqlClient;
using ServiceConnect.Persistance.SqlServer;
using Xunit;

namespace ServiceConnect.IntegrationTests
{
    public class SqlServerConnectionStringTests
    {
        [Fact]
        public void ShouldNotEncryptWhenTheConnectionStringDoesNotSayTo()
        {
            var result = new SqlConnectionStringBuilder(SqlServerProcessManagerFinder.WithUnencryptedDefault("Server=db;User ID=sa;Password=p"));

            Assert.Equal(SqlConnectionEncryptOption.Optional, result.Encrypt);
            Assert.Equal("db", result.DataSource);
            Assert.Equal("p", result.Password);
        }

        [Theory]
        [InlineData("Server=db;Encrypt=True")]
        [InlineData("Server=db;Encrypt=Strict")]
        [InlineData("Server=db;Encrypt=False")]
        public void ShouldKeepAnEncryptSettingTheConnectionStringHas(string connectionString)
        {
            Assert.Equal(connectionString, SqlServerProcessManagerFinder.WithUnencryptedDefault(connectionString));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not a connection string")]
        public void ShouldLeaveAMissingOrMalformedConnectionStringAlone(string connectionString)
        {
            Assert.Equal(connectionString, SqlServerProcessManagerFinder.WithUnencryptedDefault(connectionString));
        }
    }
}
