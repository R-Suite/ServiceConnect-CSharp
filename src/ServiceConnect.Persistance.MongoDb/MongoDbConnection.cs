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

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistance.MongoDb
{
    /// <summary>
    /// Opens a database from a standard MongoDB connection string.
    /// </summary>
    /// <remarks>
    /// Data written by earlier versions stores GUIDs as CSharpLegacy (binary subtype 3), and MongoDB.Driver 3
    /// has no default representation. Unless the application has already registered its own Guid serializer,
    /// a CSharpLegacy one is registered on first use. The stored <c>Data</c> member may hold any process
    /// manager data or message type, so it gets its own allow-list rather than the driver's default one.
    /// </remarks>
    internal static class MongoDbConnection
    {
        static MongoDbConnection()
        {
            BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.CSharpLegacy));

            var processManagerConvention = BsonSerializer.LookupDiscriminatorConvention(typeof(IProcessManagerData));
            var processManagerDataSerializer = new DiscriminatedInterfaceSerializer<IProcessManagerData>(
                processManagerConvention,
                null,
                new ObjectSerializer(processManagerConvention, GuidRepresentation.CSharpLegacy,
                    t => typeof(IProcessManagerData).IsAssignableFrom(t) || ObjectSerializer.DefaultAllowedTypes(t)));
            BsonClassMap.TryRegisterClassMap<MongoDbData<IProcessManagerData>>(cm =>
            {
                cm.AutoMap();
                cm.MapMember(c => c.Data).SetSerializer(processManagerDataSerializer);
            });

            var messageSerializer = new ObjectSerializer(BsonSerializer.LookupDiscriminatorConvention(typeof(object)),
                GuidRepresentation.CSharpLegacy,
                t => typeof(Message).IsAssignableFrom(t) || ObjectSerializer.DefaultAllowedTypes(t));
            BsonClassMap.TryRegisterClassMap<MongoDbData<object>>(cm =>
            {
                cm.AutoMap();
                cm.MapMember(c => c.Data).SetSerializer(messageSerializer);
            });
        }

        public static IMongoDatabase GetDatabase(string connectionString, string databaseName)
        {
            return new MongoClient(connectionString).GetDatabase(databaseName);
        }
    }
}
