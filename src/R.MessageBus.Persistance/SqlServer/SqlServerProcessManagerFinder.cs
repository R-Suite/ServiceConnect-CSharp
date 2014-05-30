using System;
using System.Data;
using System.Data.SqlClient;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Persistance.SqlServer
{
    public class SqlServerProcessManagerFinder : IProcessManagerFinder
    {
        private readonly string _connectionString;

        public SqlServerProcessManagerFinder(string connectionString)
        {
            _connectionString = connectionString;
        }

        public IPersistanceData<T> FindData<T>(Guid id) where T : class, IProcessManagerData
        {
            throw new NotImplementedException();
        }

        public void InsertData(IProcessManagerData data)
        {
            using(var sqlConnection=new SqlConnection(_connectionString))
            {
                string tableName = GetTableName(data);

                // todo: create table if it doesn't exist

                var sqlServerData = new SqlServerData<IProcessManagerData>
                {
                    Data = data,
                    //Version = 1,
                    //Id = Guid.NewGuid()
                };

                // todo: insert if doesn't exist, else update (only the first one is allowed)
                string insertSql =
                    string.Format("INSERT into {0} (CorrelationId,userID,idDepartment) VALUES (@staffName,@userID,@idDepartment)", tableName);

                using (var command = new SqlCommand(insertSql))
                {
                    command.Connection = sqlConnection;
                    command.Parameters.Add("@CorrelationId", SqlDbType.UniqueIdentifier).Value = data.CorrelationId;

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

        private static string GetTableName<T>(T data) where T : class, IProcessManagerData
        {
            Type typeParameterType = data.GetType();
            var tableName = typeParameterType.Name;
            return tableName;
        }
    }
}
