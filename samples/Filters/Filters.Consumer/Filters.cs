using System;
using System.Text;
using Filters.Messages;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;

namespace Filters.Consumer
{
    public class BeforeFilter1 : IFilter
    {
        public bool Process(Envelope envelope)
        {
            Console.WriteLine("Inside before filter 1");
            var json = Encoding.UTF8.GetString(envelope.Body);
            var message = JsonConvert.DeserializeObject<FilterMessage>(json);
            message.FilterModifiedValue = "modified by consumer";
            envelope.Body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
            return true;
        }

        public IBus Bus { get; set; }
    }

    public class BeforeFilter2 : IFilter
    {
        public bool Process(Envelope envelope)
        {
            var json = Encoding.UTF8.GetString(envelope.Body);
            var message = JsonConvert.DeserializeObject<FilterMessage>(json);

            Console.WriteLine("Inside before filter 2");

            if (message.ConsumerFilterFail)
            {
                return false;
            }
            
            return true;
        }

        public IBus Bus { get; set; }
    }

    public class AfterFilter1 : IFilter
    {
        public bool Process(Envelope envelope)
        {
            Console.WriteLine("Inside after filter 1");
            return true;
        }

        public IBus Bus { get; set; }
    }

    public class AfterFilter2: IFilter
    {
        public bool Process(Envelope envelope)
        {
            Console.WriteLine("Inside after filter 2");
            return true;
        }

        public IBus Bus { get; set; }
    }
}