using System;
using System.Collections.Generic;
using System.Security.Authentication;
using McDonalds.Messages;
using ServiceConnect;
using ServiceConnect.Interfaces;

namespace McDonalds.Customer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Customer ***********");
            IBus bus = Bus.Initialize(x =>
            {
                //x.TransportSettings.SslEnabled = true;
                //x.TransportSettings.CertPassphrase = "secret";
                //x.TransportSettings.CertPath = "path";
                //x.TransportSettings.Username = "admin";
                //x.TransportSettings.Password = "password";
                //x.TransportSettings.ServerName = "node1,node2,node3";
                //x.TransportSettings.Version = SslProtocols.Default;
                //x.SetHost("node1,node2,node3");
                x.SetHost("localhost");
            });
            bus.StartConsuming();

            while (true)
            {
                Console.WriteLine("Options:\n--------");
                Console.WriteLine("1. To place new order");
                //Console.WriteLine("<ENTER> To exit");
                var selectedOption = SelectOption();

                switch (selectedOption)
                {
                    case 1:
                        PlaceNewOrder(bus);
                        break;
                }
            }

        }

        public static List<string> Meals = new List<string>
        {
            "Burger Meal",
            "Big Mac Meal",
            "Cheese Burger Meal"
        };

        public static List<string> Sizes = new List<string>
        {
            "XL",
            "Large",
            "Medium",
            "Small"
        };

        static readonly Random Random = new Random();

        private static void PlaceNewOrder(IBus bus)
        {
            var meal = Meals[Random.Next(0, 2)];
            var size = Sizes[Random.Next(0, 3)];

            bus.Publish(new NewOrderMessage(Guid.NewGuid()) { Name = meal, Size = size });
        }

        private static int SelectOption()
        {
            int selectedOption;
            if (!Int32.TryParse(Console.ReadLine(), out selectedOption))
            {
                Console.WriteLine("Error, try again.");
                SelectOption();
            }

            return selectedOption;
        }
    }
}
