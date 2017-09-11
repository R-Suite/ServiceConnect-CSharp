using System;
using System.Security.Authentication;
using ServiceConnect;
using ServiceConnect.Persistance.MongoDb;
using ServiceConnect.Persistance.MongoDbSsl;

namespace McDonalds.Cashier
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Cashier ***********");
            var bus = Bus.Initialize(x =>
            {
                x.SetProcessManagerFinder<MongoDbProcessManagerFinder>();
                //x.SetProcessManagerFinder<MongoDbSslProcessManagerFinder>();
                //x.TransportSettings.SslEnabled = true;
                //x.TransportSettings.CertPassphrase = "secret";
                //x.TransportSettings.CertPath = "path";
                //x.TransportSettings.Username = "admin";
                //x.TransportSettings.Password = "password";
                //x.TransportSettings.ServerName = "node1,node2,node3";
                //x.TransportSettings.Version = SslProtocols.Default;
                x.SetHost("localhost");
                //x.PersistenceStoreConnectionString = @"nodes=node1;node2;node3,username=admin,password=secret,certpath=path";
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
