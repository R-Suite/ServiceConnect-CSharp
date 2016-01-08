using System;
using System.Diagnostics;
using System.Reflection;
using System.ServiceProcess;
using BusDisposeTest.Messages;
using log4net;
using log4net.Config;
using Ruffer.Wcf.MonitorService.Contracts;
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
            XmlConfigurator.Configure();
            Logger.DebugFormat("Starting {0} service", ServiceName);

            //ObjectFactory.Configure(c => c.AddRegistry<SmRegistry>());

            // Start Bus
            _bus = Bus.Initialize(config =>
            {
                //config.SetThreads(2);
                //config.SetContainer(ObjectFactory.Container);
                config.SetContainerType<DefaultBusContainer>();
                config.SetHost("lonappdev01");
                config.TransportSettings.ClientSettings.Add("PrefetchCount", 100);
                config.ScanForMesssageHandlers = true;
                config.AddBusToContainer = true;
            });
            _bus.StartConsuming();

            _bus.Publish(new TestMsg(Guid.NewGuid()));

            try
            {
                var processToRegister = GetCurrentProcessMonitoringInfo();
                MonitorBrokerChannel.GetInstance().RegisterProcess(processToRegister);
            }
            catch (Exception ex)
            {
                Logger.Warn("Cannot register the process with Ruffer Service Management.", ex);
            }
        }

        private static void Stop()
        {
            _bus.Dispose();
            Logger.DebugFormat("Stopping {0} service", ServiceName);
        }

        private static ManagedProcess GetCurrentProcessMonitoringInfo()
        {
            var processType = ProcessType.WindowsService;
            string windowsServiceName = "Ruffer.TradeRepository.Host";

            if (Environment.UserInteractive)
            {
                processType = ProcessType.Console;
                windowsServiceName = "na";
            }

            var processToRegister = new ManagedProcess();
            processToRegister.ApplicationId = "Ruffer.TradeRepository";
            processToRegister.Host = System.Net.Dns.GetHostName();
            processToRegister.FilePath = Process.GetCurrentProcess().Modules[0].FileName;
            processToRegister.ProcessType = processType;
            processToRegister.ProcessId = Process.GetCurrentProcess().Id;
            processToRegister.ServiceId = "Ruffer.TradeRepository.Host";
            processToRegister.WindowsServiceName = windowsServiceName;

            return processToRegister;
        }
    }
}
