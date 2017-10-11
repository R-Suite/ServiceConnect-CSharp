using System;
using System.Text;
using Filters.Messages;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;

namespace Filters
{
    public class Filter1 : IFilter
    {
        private string _p1;

        public Filter1(string p1)
        {
            _p1 = p1;
        }

        public bool Process(Envelope envelope)
        {
            Console.WriteLine(_p1);
            Console.WriteLine("Inside outgoing filter 1");
            var json = Encoding.UTF8.GetString(envelope.Body);
            var message = JsonConvert.DeserializeObject<FilterMessage>(json);
            message.FilterModifiedValue = "modified by producer";
            envelope.Body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));

            return true;
        }

        public IBus Bus { get; set; }
    }

    public class Filter2 : IFilter
    {
        public bool Process(Envelope envelope)
        {
            var json = Encoding.UTF8.GetString(envelope.Body);
            var message = JsonConvert.DeserializeObject<FilterMessage>(json);

            Console.WriteLine("Inside outgoing filter 2");

            if (message.ProducerFilterFail)
            {
                return false;
            }

            return true;
        }

        public IBus Bus { get; set; }
    }
}