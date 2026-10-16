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

using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistance.MongoDb
{
    /// <summary>
    /// Sets up BSON serialization for the persistence classes. Runs when one of them is constructed.
    /// </summary>
    /// <remarks>
    /// Earlier versions stored GUIDs as CSharpLegacy (binary subtype 3), and MongoDB.Driver 3 has no default
    /// representation. If the application has not chosen one, CSharpLegacy is registered. If it has, its choice
    /// is kept. The stored <c>Data</c> member may hold any process manager data or message type, so it gets its
    /// own allow-list rather than the driver's default one.
    /// </remarks>
    internal static class MongoDbSerialization
    {
        private static readonly object Lock = new object();
        private static bool _configured;

        public static void Configure()
        {
            lock (Lock)
            {
                if (_configured)
                {
                    return;
                }

                EnsureGuidRepresentation(
                    serializer => BsonSerializer.TryRegisterSerializer(serializer),
                    () => BsonSerializer.LookupSerializer<Guid>());
                RegisterClassMaps();
                _configured = true;
            }
        }

        /// <exception cref="InvalidOperationException">
        /// The process already uses a Guid serializer with no representation, which cannot be replaced.
        /// </exception>
        internal static void EnsureGuidRepresentation(Func<GuidSerializer, bool> tryRegister, Func<IBsonSerializer<Guid>> lookup)
        {
            try
            {
                tryRegister(new GuidSerializer(GuidRepresentation.CSharpLegacy));
                return;
            }
            catch (BsonSerializationException)
            {
                // A different Guid serializer is already registered or cached.
            }

            if (lookup() is GuidSerializer { Representation: BsonType.Binary, GuidRepresentation: GuidRepresentation.Unspecified })
            {
                throw new InvalidOperationException(
                    "MongoDB is already using a Guid serializer with no GuidRepresentation in this process, so " +
                    "ServiceConnect cannot store GUIDs. Register one at startup, before any MongoDB serialization, for " +
                    "example BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.CSharpLegacy)). " +
                    "CSharpLegacy reads data written by earlier versions of ServiceConnect.");
            }
        }

        private static void RegisterClassMaps()
        {
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
    }
}
