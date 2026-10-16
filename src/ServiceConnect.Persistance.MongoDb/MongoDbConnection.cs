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

using MongoDB.Driver;

namespace ServiceConnect.Persistance.MongoDb
{
    /// <summary>
    /// Opens a database from a standard MongoDB connection string.
    /// </summary>
    internal static class MongoDbConnection
    {
        public static IMongoDatabase GetDatabase(string connectionString, string databaseName)
        {
            MongoDbSerialization.Configure();
            return new MongoClient(connectionString).GetDatabase(databaseName);
        }
    }
}
