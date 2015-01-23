using System;
using System.Data;
using System.Data.SqlClient;
using Newtonsoft.Json;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Persistance.SqlServer
{
    /// <summary>
    /// Sql Server implementation of IProcessManagerFinder.
    /// </summary>
    public class SqlServerProcessManagerFinder : IProcessManagerFinder
    {
        private readonly string _connectionString;
        private readonly int _commandTimeout = 30;

        /// <summary>
        /// Default constructor
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseName"></param>
        public SqlServerProcessManagerFinder(string connectionString, string databaseName)
        {
            _connectionString = connectionString;
        }

        /// <summary>
        /// Constructor allows passing <see cref="commandTimeout"/>.
        /// Used primarily for testing.
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseName"></param>
        /// <param name="commandTimeout"></param>
        public SqlServerProcessManagerFinder(string connectionString, string databaseName, int commandTimeout)
        {
            _connectionString = connectionString;
            _commandTimeout = commandTimeout;
        }

        /// <summary>
        /// Find existing instance of ProcessManager
        /// FindData() and UpdateData() are part of the same transaction.
        /// FindData() opens new connection and transaction. 
        /// UPDLOCK is placed onf the relevant row to prevent reads until the transaction is commited in UpdateData
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="id"></param>
        /// <returns></returns>
        public IPersistanceData<T> FindData<T>(Guid id) where T : class, IProcessManagerData
        {
            SqlServerData<T> result = null;

            string tableName = typeof(T).Name;

            if (!GetTableNameExists(tableName))
                return null;

            var connection = new SqlConnection(_connectionString);
            connection.Open();

            try
            {
                using (var command = new SqlCommand())
                {
                    command.Connection = connection;
                    command.CommandTimeout = _commandTimeout;
                    command.CommandText = string.Format(@"SELECT * FROM {0} WHERE Id = @Id", tableName);
                    command.Parameters.Add(new SqlParameter {ParameterName = "@Id", Value = id});
                    var reader = command.ExecuteReader(CommandBehavior.SingleResult);

                    if (reader.HasRows)
                    {
                        reader.Read();

                        var settings = new JsonSerializerSettings
                        {
                            TypeNameHandling = TypeNameHandling.Objects
                        };

                        var data = JsonConvert.DeserializeObject<T>(reader["DataJson"].ToString(), settings);

                        result = new SqlServerData<T>
                        {
                            Id = (Guid) reader["Id"],
                            Data = data,
                            Version = (int) reader["Version"]
                        };
                    }

                    reader.Close();
                }
            }
            finally
            {
                connection.Close();
            }

            return result;
        }

        public IPersistanceData<T> FindData<T>(ProcessManagerPropertyMapper mapper, Message message) where T : class, IProcessManagerData
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Create new instance of ProcessManager
        /// When multiple threads try to create new ProcessManager instance, only the first one is allowed. 
        /// All subsequent threads will update data instead.
        /// </summary>
        /// <param name="data"></param>
        public void InsertData(IProcessManagerData data)
        {
            string tableName = GetTableName(data);

            var sqlServerData = new SqlServerData<IProcessManagerData>
            {
                Data = data,
                Version = 1,
                Id = data.CorrelationId
            };

            var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Objects };
            var dataJson = JsonConvert.SerializeObject(sqlServerData.Data, Formatting.Indented, settings);

            using (var sqlConnection = new SqlConnection(_connectionString))
            {
                sqlConnection.Open();

                using (var dbTransaction = sqlConnection.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    // Insert if doesn't exist, else update (only the first one is allowed)
                    string upsertSql = string.Format(@"if exists (select * from {0} with (updlock,serializable) WHERE Id = @Id)
                                                    begin
                                                        UPDATE {0}
		                                                SET DataJson = @DataJson, Version = @Version 
		                                                WHERE Id = @Id
                                                    end
                                                else
                                                    begin
                                                        INSERT {0} (Id, Version, DataJson)
                                                        VALUES (@Id,@Version,@DataJson)
                                                    end", tableName);


                    var command = new SqlCommand(upsertSql) {Transaction = dbTransaction, Connection = sqlConnection};
                    command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = data.CorrelationId;
                    command.Parameters.Add("@Version", SqlDbType.Int).Value = sqlServerData.Version;
                    command.Parameters.Add("@DataJson", SqlDbType.Text).Value = dataJson;

                    try
                    {
                        command.ExecuteNonQuery();
                        dbTransaction.Commit();
                    }
                    catch
                    {
                        dbTransaction.Rollback();
                        throw;
                    }
                    finally
                    {
                        sqlConnection.Close();
                    }
                }
            }
        }

        /// <summary>
        /// Update data of existing ProcessManager and completes transaction opened by FindData().
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        public void UpdateData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData
        {
            string tableName = GetTableName(data.Data);

            var sqlServerData = (SqlServerData<T>)data;
            int currentVersion = sqlServerData.Version;

            var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Objects };
            var dataJson = JsonConvert.SerializeObject(sqlServerData.Data, Formatting.Indented, settings);

            string sql = string.Format(@"UPDATE {0} SET DataJson = @DataJson, Version = @NewVersion WHERE Id = @Id AND Version = @CurrentVersion", tableName);

            var connection = new SqlConnection(_connectionString);
            connection.Open();

            int result;

            try
            {
                var command = new SqlCommand(sql) {Connection = connection, CommandTimeout = _commandTimeout};
                command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = sqlServerData.Id;
                command.Parameters.Add("@DataJson", SqlDbType.Text).Value = dataJson;
                command.Parameters.Add("@CurrentVersion", SqlDbType.Int).Value = currentVersion;
                command.Parameters.Add("@NewVersion", SqlDbType.Int).Value = ++currentVersion;
                result = command.ExecuteNonQuery();
            }
            finally
            {
                connection.Close();
            }

            if (result == 0)
                throw new ArgumentException(string.Format("Possible Concurrency Error. ProcessManagerData with CorrelationId {0} and Version {1} could not be updated.", sqlServerData.Data.CorrelationId, sqlServerData.Version));
        }

        /// <summary>
        /// Removes existing instance of ProcessManager from the database and 
        /// completes transaction opened by FindData().
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        public void DeleteData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData
        {
            string tableName = GetTableName(data.Data);

            var sqlServerData = (SqlServerData<T>)data;

            string sql = string.Format(@"DELETE FROM {0} WHERE Id = @Id", tableName);

            var connection = new SqlConnection(_connectionString);
            connection.Open();

            try
            {
                var command = new SqlCommand(sql) {Connection = connection};
                command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = sqlServerData.Id;
                command.ExecuteNonQuery();
            }
            finally
            {
                connection.Close();
            }
        }

        #region Private Methods

        private bool GetTableNameExists(string tableName)
        {
            bool retval = false;

            // Create table if doesn't exist
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand())
                {
                    command.Connection = connection;
                    command.CommandText = string.Format("SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{0}'", tableName);
                    var result = command.ExecuteScalar();

                    if (null != result && (int)result == 1)
                        retval = true;
                }
            }

            return retval;
        }

        private string GetTableName<T>(T data) where T : class, IProcessManagerData
        {
            Type typeParameterType = data.GetType();
            var tableName = typeParameterType.Name;

            // Create table if doesn't exist
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand())
                {
                    command.Connection = connection;
                    command.CommandText = string.Format("IF NOT EXISTS( SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{0}') ", tableName) +
                        string.Format("CREATE TABLE {0} (Id uniqueidentifier NOT NULL, Version int NOT NULL, DataJson text NULL);", tableName);
                    command.ExecuteNonQuery();
                }
                connection.Close();
            }

            return tableName;
        }

        #endregion
    }
}
