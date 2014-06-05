using System;
using System.Data;
using System.Data.SqlClient;
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
        private SqlConnection _connection;
        private SqlTransaction _dbTransaction;

        /// <summary>
        /// Default constructor
        /// </summary>
        /// <param name="connectionString"></param>
        public SqlServerProcessManagerFinder(string connectionString)
        {
            _connectionString = connectionString;
        }

        /// <summary>
        /// Constructor allows passing <see cref="commandTimeout"/>.
        /// Used primarily for testing.
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="commandTimeout"></param>
        public SqlServerProcessManagerFinder(string connectionString, int commandTimeout)
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

            _connection = new SqlConnection(_connectionString); // will be closed by UpdateData or Delete method
            _connection.Open();
            _dbTransaction = _connection.BeginTransaction(IsolationLevel.ReadCommitted);    // will be commited by UpdateData or Delete methods          

            try
            {
                using (var command = new SqlCommand())
                {
                    command.Connection = _connection;
                    command.CommandTimeout = _commandTimeout;
                    command.Transaction = _dbTransaction;
                    command.CommandText = string.Format(@"SELECT * FROM {0} WITH (UPDLOCK) WHERE Id = @Id", tableName);
                    command.Parameters.Add(new SqlParameter { ParameterName = "@Id", Value = id });
                    var reader = command.ExecuteReader(CommandBehavior.SingleResult);

                    if (reader.HasRows)
                    {
                        reader.Read();

                        var data = Newtonsoft.Json.JsonConvert.DeserializeObject<T>(reader["DataJson"].ToString());

                        result = new SqlServerData<T> { Id = (Guid)reader["Id"], Data = data };
                    }

                    reader.Close();
                }
            }
            catch
            {
                _dbTransaction.Rollback();
                _connection.Close();
                throw;
            }

            if (null == result)
            {
                _dbTransaction.Rollback();
                _connection.Close();
            }

            return result;
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

            using (var sqlConnection = new SqlConnection(_connectionString))
            {
                var sqlServerData = new SqlServerData<IProcessManagerData>
                {
                    Data = data,
                    Id = data.CorrelationId
                };

                var sqlServerDataJson = Newtonsoft.Json.JsonConvert.SerializeObject(sqlServerData);

                // Insert if doesn't exist, else update (only the first one is allowed)
                string upsertSql = string.Format(@"begin tran
                                       UPDATE {0} with (serializable)
                                       SET DataJson = @DataJson
                                       WHERE Id = @Id
                                       if @@ROWCOUNT = 0
                                       begin
                                          INSERT {0} (Id, DataJson)
                                          VALUES (@Id,@DataJson)
                                       end
                                    commit tran", tableName);

                sqlConnection.Open();

                using (var command = new SqlCommand(upsertSql))
                {
                    command.Connection = sqlConnection;
                    command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = data.CorrelationId;
                    command.Parameters.Add("@DataJson", SqlDbType.Text).Value = sqlServerDataJson;
                    command.ExecuteNonQuery();
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
            if (null == _connection || _connection.State != ConnectionState.Open)
                throw new Exception("No open database connection found.");

            if (null == _dbTransaction)
                throw new Exception("No available database transaction found.");

            string tableName = GetTableName(data.Data);

            var sqlServerData = (SqlServerData<T>)data;

            var sqlServerDataJson = Newtonsoft.Json.JsonConvert.SerializeObject(sqlServerData);

            string sql = string.Format(@"UPDATE {0} SET DataJson = @DataJson WHERE Id = @Id", tableName);

            try
            {
                var command = new SqlCommand(sql);
                command.Connection = _connection;
                command.Transaction = _dbTransaction;
                command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = sqlServerData.Id;
                command.Parameters.Add("@DataJson", SqlDbType.Text).Value = sqlServerDataJson;
                command.ExecuteNonQuery();
                _dbTransaction.Commit();
            }
            catch
            {
                _dbTransaction.Rollback();
                throw;
            }
            finally
            {
                _connection.Close();
            }
        }

        /// <summary>
        /// Removes existing instance of ProcessManager from the database and 
        /// completes transaction opened by FindData().
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        public void DeleteData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData
        {
            if (null == _connection || _connection.State != ConnectionState.Open)
                throw new Exception("No open database connection found.");

            if (null == _dbTransaction)
                throw new Exception("No available database transaction found.");

            string tableName = GetTableName(data.Data);

            var sqlServerData = (SqlServerData<T>)data;

            string sql = string.Format(@"DELETE FROM {0} WHERE Id = @Id", tableName);

            try
            {
                var command = new SqlCommand(sql);
                command.Connection = _connection;
                command.Transaction = _dbTransaction;
                command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = sqlServerData.Id;
                command.ExecuteNonQuery();
                _dbTransaction.Commit();
            }
            catch
            {
                _dbTransaction.Rollback();
                throw;
            }
            finally
            {
                _connection.Close();
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
                        string.Format("CREATE TABLE {0} (Id uniqueidentifier NOT NULL, DataJson text NULL);", tableName);
                    command.ExecuteNonQuery();
                }
            }

            return tableName;
        }

        #endregion
    }
}
