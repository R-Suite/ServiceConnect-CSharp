using System;
using System.Reflection;
using System.ServiceProcess;
using Common.Logging;
using ServiceConnect;
using ServiceConnect.Interfaces;

namespace ServiceConnect.EndpointHost.VsProjectTemplate
{
    /// <summary>
    /// ServiceConnect Endpoint Host that can run as Console App or 
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
            Logger.DebugFormat("Starting {0} service", ServiceName);

            // Start Bus
            _bus = Bus.Initialize();
        }

        private static void Stop()
        {
            Logger.DebugFormat("Stopping {0} service", ServiceName);
            _bus.Dispose();
        }
    }
}
