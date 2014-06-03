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

        public SqlServerProcessManagerFinder(string connectionString)
        {
            _connectionString = connectionString;
        }

        /// <summary>
        /// Find existing instance of ProcessManager
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="id"></param>
        /// <returns></returns>
        public IPersistanceData<T> FindData<T>(Guid id) where T : class, IProcessManagerData
        {
            SqlServerData<T> result;

            string tableName = typeof(T).Name;

            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand())
                {
                    command.Connection = connection;
                    command.CommandText = string.Format(@"SELECT * FROM {0} WHERE Id = @Id", tableName);
                    command.Parameters.Add(new SqlParameter {ParameterName = "@Id", Value = id});
                    var reader = command.ExecuteReader(CommandBehavior.SingleResult);
                    reader.Read();

                    var data = Newtonsoft.Json.JsonConvert.DeserializeObject<T>(reader["DataJson"].ToString());

                    result = new SqlServerData<T> { Id = (Guid)reader["Id"], Data = data };
                }
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

            using(var sqlConnection = new SqlConnection(_connectionString))
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

                using (var command = new SqlCommand(upsertSql))
                {
                    command.Connection = sqlConnection;
                    command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = data.CorrelationId;
                    command.Parameters.Add("@DataJson", SqlDbType.Text).Value = sqlServerDataJson;

                    try
                    {
                        sqlConnection.Open();
                        int recordsAffected = command.ExecuteNonQuery();
                    }
                    catch (SqlException ex)
                    {
                        // todo: error here
                    }
                    finally
                    {
                        sqlConnection.Close();
                    }
                }
            }
        }

        public void UpdateData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData
        {
            throw new NotImplementedException();
        }

        public void DeleteData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData
        {
            throw new NotImplementedException();
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
    }
}
