using System;
using System.Diagnostics;
using System.Reflection;
using System.ServiceProcess;
using BusDisposeTest.Messages;
using Common.Logging;
using ServiceConnect;
using ServiceConnect.Container.Default;
using ServiceConnect.Interfaces;

namespace BusDisposeTest
{
    /// <summary>
    /// R.MessageBus Endpoint Host that can run as Console App or 
    /// can be installed as Windows Services
    /// </summary>
    public class Program
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static IBus _bus;

        #region Nested classes to support running as service

        private const string ServiceName = "MyService"; // modify to use your own Service Name

        public class Service : ServiceBase
        {
            public Service()
            {
                ServiceName = ServiceName;
            }

            protected override void OnStart(string[] args)
            {
                Program.Start(args);
            }

            protected override void OnStop()
            {
                Program.Stop();
            }
        }

        #endregion

        static void Main(string[] args)
        {
            if (!Environment.UserInteractive)
                // running as service
                using (var service = new Service())
                    ServiceBase.Run(service);
            else
            {
                // running as console app
                Start(args);

                Console.WriteLine();
                Console.WriteLine("Press any key to stop...");
                Console.ReadKey(true);

                Stop();
            }
        }

        private static void Start(string[] args)
        {
            
            // Start Bus
            _bus = Bus.Initialize(config =>
            {
                //config.SetNumberOfClients(2);
                //config.SetContainer(ObjectFactory.Container);
                config.SetContainerType<DefaultBusContainer>();
                config.SetHost("localhost");
                config.TransportSettings.ClientSettings.Add("PrefetchCount", 100);
                config.ScanForMesssageHandlers = true;
                config.AddBusToContainer = true;
            });
            _bus.StartConsuming();

            _bus.Publish(new TestMsg(Guid.NewGuid()));
        }

        private static void Stop()
        {
            _bus.Dispose();
        }
    }
}
