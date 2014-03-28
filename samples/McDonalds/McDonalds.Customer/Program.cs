using System;
using McDonalds.Messages;
using R.MessageBus;
using R.MessageBus.Interfaces;

namespace McDonalds.Customer
{
    class Program
    {
        static void Main(string[] args)
        {
            IBus bus = Bus.Initialize(config =>
            {
                config.ScanForMesssageHandlers = true;
            });

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

        private static void PlaceNewOrder(IBus bus)
        {
            bus.Publish(new NewOrderMessage(Guid.NewGuid()) { Name = "Burger Meal", Size = "Large"});
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
