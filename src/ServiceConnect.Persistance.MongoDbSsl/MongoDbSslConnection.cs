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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistance.MongoDbSsl
{
    /// <summary>
    /// Opens a TLS connection from a ServiceConnect SSL connection string:
    /// <c>nodes=host:port;host:port,username=,password=,userdb=,certpath=|cert=,certpassword=</c>.
    /// </summary>
    /// <remarks>
    /// Data written by earlier versions stores GUIDs as CSharpLegacy (binary subtype 3), and MongoDB.Driver 3
    /// has no default representation. Unless the application has already registered its own Guid serializer,
    /// a CSharpLegacy one is registered on first use. The stored <c>Data</c> member may hold any process
    /// manager data or message type, so it gets its own allow-list rather than the driver's default one.
    /// </remarks>
    internal static class MongoDbSslConnection
    {
        static MongoDbSslConnection()
        {
            BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.CSharpLegacy));

            var processManagerConvention = BsonSerializer.LookupDiscriminatorConvention(typeof(IProcessManagerData));
            var processManagerDataSerializer = new DiscriminatedInterfaceSerializer<IProcessManagerData>(
                processManagerConvention,
                null,
                new ObjectSerializer(processManagerConvention, GuidRepresentation.CSharpLegacy,
                    t => typeof(IProcessManagerData).IsAssignableFrom(t) || ObjectSerializer.DefaultAllowedTypes(t)));
            BsonClassMap.TryRegisterClassMap<MongoDbSslData<IProcessManagerData>>(cm =>
            {
                cm.AutoMap();
                cm.MapMember(c => c.Data).SetSerializer(processManagerDataSerializer);
            });

            var messageSerializer = new ObjectSerializer(BsonSerializer.LookupDiscriminatorConvention(typeof(object)),
                GuidRepresentation.CSharpLegacy,
                t => typeof(Message).IsAssignableFrom(t) || ObjectSerializer.DefaultAllowedTypes(t));
            BsonClassMap.TryRegisterClassMap<MongoDbSslData<object>>(cm =>
            {
                cm.AutoMap();
                cm.MapMember(c => c.Data).SetSerializer(messageSerializer);
            });
        }

        public static IMongoDatabase GetDatabase(string connectionString, string databaseName)
        {
            string nodes = string.Empty;
            string username = string.Empty;
            string password = string.Empty;
            string certPath = string.Empty;
            string userdb = string.Empty;
            string cert = string.Empty;
            string certPassword = string.Empty;

            foreach (string connectionPart in connectionString.Split(','))
            {
                var assignmentIndex = connectionPart.IndexOf('=');
                var nameValue = connectionPart.Substring(0, assignmentIndex);
                var value = connectionPart.Substring(assignmentIndex + 1);

                switch (nameValue.ToLower())
                {
                    case "nodes":
                        nodes = value;
                        break;
                    case "userdb":
                        userdb = value;
                        break;
                    case "username":
                        username = value;
                        break;
                    case "password":
                        password = value;
                        break;
                    case "certpath":
                        certPath = value;
                        break;
                    case "cert":
                        cert = value;
                        break;
                    case "certpassword":
                        certPassword = value;
                        break;
                }
            }

            List<X509Certificate> certs = null;
            if (!string.IsNullOrEmpty(cert))
            {
                certs = new List<X509Certificate> { LoadCertificate(Convert.FromBase64String(cert), certPassword) };
            }
            else if (!string.IsNullOrEmpty(certPath))
            {
                certs = new List<X509Certificate> { LoadCertificate(File.ReadAllBytes(certPath), certPassword) };
            }

            MongoCredential credential = null;
            if (!string.IsNullOrEmpty(username))
            {
                var db = string.IsNullOrEmpty(userdb) ? "admin" : userdb;
                credential = MongoCredential.CreateCredential(db, username, password);
            }

            var settings = new MongoClientSettings
            {
                UseTls = true,
                Credential = credential,
                Servers = nodes.Split(';').Select(x => MongoServerAddress.Parse(x)),
                SslSettings = certs == null ? null : new SslSettings
                {
                    ClientCertificates = certs,
                    ClientCertificateSelectionCallback = (sender, host, certificates, certificate, issuers) => certificates[0],
                    CheckCertificateRevocation = false
                }
            };

            return new MongoClient(settings).GetDatabase(databaseName);
        }

        private static X509Certificate2 LoadCertificate(byte[] data, string password)
        {
            if (X509Certificate2.GetCertContentType(data) == X509ContentType.Pkcs12)
            {
                return X509CertificateLoader.LoadPkcs12(data, string.IsNullOrEmpty(password) ? null : password);
            }

            return X509CertificateLoader.LoadCertificate(data);
        }
    }
}
