using System;
using System.Collections.Generic;
using R.MessageBus;
using R.MessageBus.Interfaces;
using RecipientList.Messages;

namespace RecipientList.Publisher
{
    class Program
    {
        private static IBus _bus;

        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            _bus = Bus.Initialize(config =>
            {
            });
            _bus.StartConsuming();

            while (true)
            {
                Console.WriteLine("Choose a option");
                Console.WriteLine("1 Recipient List");
                Console.WriteLine("2 Recipient List Reply Sync");
                Console.WriteLine("3 Recipient List Reply Async");
                Console.WriteLine("4 Recipient List Timeout (Should only receive a reply from Consumer 2)");

                var result = Console.ReadLine();
                switch (result)
                {
                    case "1":
                        TestRecipientList();
                        break;
                    case "2":
                        TestRecipientListReplySynch();
                        break;
                    case "3":
                        TestRecipientListReplyAsynch();
                        break;
                    case "4":
                        TestRecipientListReplySynchTimeout();
                        break;
                }
            }
        }

        private static void TestRecipientListReplySynchTimeout()
        {
            var id = Guid.NewGuid();
            var responses = _bus.SendRequest<RecipientListMessage, RecipientListResponse>(
                new List<string>
                    {
                        "Consumer1",
                        "Consumer2"
                    },
                new RecipientListMessage(id)
                {
                    SendReply = true,
                    Delay = true
                }, timeout: 500
            );

            foreach (RecipientListResponse response in responses)
            {
                Console.WriteLine("Received response from - {0}", response.Endpoint);
            }

            Console.WriteLine("");
        }

        private static void TestRecipientListReplyAsynch()
        {
            var id = Guid.NewGuid();
            _bus.SendRequest<RecipientListMessage, RecipientListResponse>(
                new List<string>
                    {
                        "Consumer1",
                        "Consumer2"
                    },
                new RecipientListMessage(id)
                {
                    SendReply = true
                },
                r =>
                {
                    foreach (RecipientListResponse recipientListResponse in r)
                    {
                        Console.WriteLine("Received response from - {0}", recipientListResponse.Endpoint); 
                    }
                    Console.WriteLine("");
                }
            );

            Console.WriteLine("");
        }

        private static void TestRecipientListReplySynch()
        {
            var id = Guid.NewGuid();
            var responses = _bus.SendRequest<RecipientListMessage, RecipientListResponse>(
                new List<string>
                    {
                        "Consumer1",
                        "Consumer2"
                    },
                new RecipientListMessage(id)
                {
                    SendReply = true
                }
            );

            foreach (RecipientListResponse response in responses)
            {
                Console.WriteLine("Received response from - {0}", response.Endpoint);
            }

            Console.WriteLine("");
        }

        private static void TestRecipientList()
        {
            var id = Guid.NewGuid();
            _bus.Send(
                new List<string>
                    {
                        "Consumer1",
                        "Consumer2"
                    },
                new RecipientListMessage(id)
            );

            Console.WriteLine("Sent message to consumer 1 and 2 - {0}", id);
            Console.WriteLine("");
        }
    }
}
