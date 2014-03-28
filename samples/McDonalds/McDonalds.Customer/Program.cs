using System;
using R.MessageBus;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Container;
using R.MessageBus.Interfaces;

namespace McDonalds.Customer
{
    class Program
    {
        static void Main(string[] args)
        {
            IBus bus = Bus.Initialize(config =>
            {
                config.SetConsumer<Consumer>();
                config.SetPublisher<Publisher>();
                config.SetContainer<StructuremapContainer>();
                config.ScanForMesssageHandlers = true;
            });

            Console.WriteLine("Options:");
            Console.WriteLine("--------");
            Console.WriteLine("1. Place new order");

            var selectedOption = SelectOption();

            switch (selectedOption)
            {
                case 1:
                    PlaceNewOrder(bus);
                    break;
            }

        }

        private static void PlaceNewOrder(IBus bus)
        {
            bus.
        }


        private static int SelectOption()
        {
            int selectedOption;
            if (!Int32.TryParse(Console.ReadKey().ToString(), out selectedOption))
            {
                Console.WriteLine("Error, try again.");
                SelectOption();
            }

            return selectedOption;
        }
    }
}
