using System;
using System.Security.Authentication;
using ServiceConnect;

namespace McDonalds.BurgerFlipper
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Burger Flipper ***********");
            var bus = Bus.Initialize(x =>
            {
                //x.TransportSettings.SslEnabled = true;
                //x.TransportSettings.CertPassphrase = "secret";
                //x.TransportSettings.CertPath = "path";
                //x.TransportSettings.Username = "admin";
                //x.TransportSettings.Password = "password";
                //x.TransportSettings.ServerName = "node1,node2,node3";
                //x.TransportSettings.Version = SslProtocols.Default;
                x.SetHost("localhost");
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
