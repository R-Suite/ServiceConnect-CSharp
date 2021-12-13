using System;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;

namespace Middleware.Consumer
{
    public class Middleware1 : IBusMiddleware
    {
        public ProcessMessageDelegate Next { get; set; }

        public async Task Process(IConsumeContext context, Type typeObject, Envelope envelope)
        {
            Console.WriteLine("Middleware 1 Start - " + typeObject.Name);
            await Next(context, typeObject, envelope);
            Console.WriteLine("Middleware 1 End - " + typeObject.Name);
        }
    }
}