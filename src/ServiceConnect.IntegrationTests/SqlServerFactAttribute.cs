using System;
using Xunit;

namespace ServiceConnect.IntegrationTests
{
    /// <summary>
    /// A test that needs a SQL Server database. It is skipped unless <c>SERVICECONNECT_SQLSERVER</c> holds a
    /// SQL Server connection string.
    /// </summary>
    public sealed class SqlServerFactAttribute : FactAttribute
    {
        public const string ConnectionStringVariable = "SERVICECONNECT_SQLSERVER";

        public SqlServerFactAttribute()
        {
            if (string.IsNullOrEmpty(ConnectionString))
            {
                Skip = $"Set {ConnectionStringVariable} to run the SQL Server integration tests.";
            }
        }

        public static string ConnectionString => Environment.GetEnvironmentVariable(ConnectionStringVariable);
    }
}
