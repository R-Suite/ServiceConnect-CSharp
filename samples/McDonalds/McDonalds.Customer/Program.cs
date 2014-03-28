using System;
using System.Collections.Generic;
using System.Threading;
using McDonalds.Messages;
using R.MessageBus;
using R.MessageBus.Interfaces;

namespace McDonalds.Customer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Customer ***********");
            IBus bus = Bus.Initialize(config =>
            {
                config.ScanForMesssageHandlers = true;
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
