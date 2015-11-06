using System;
using ProcessManager.Messages;
using ServiceConnect.Interfaces;

namespace ProcessManager.Host
{
    public class PmWidgetSize
    {
        public int Width { get; set; }
    }

    public class PmWidget
    {
        public PmWidgetSize Size { get; set; }
    }

    public class MyProcessManagerData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public string Name { get; set; }
        public int ProcessId { get; set; }
        public PmWidget PmWidget { get; set; }
    }

    public class MyProcessManager : ServiceConnect.Core.ProcessManager<MyProcessManagerData>,
        IStartProcessManager<StartProcessManagerMessage>,
        IStartProcessManager<Process1ResponseMessage>,
        IMessageHandler<Process2ResponseMessage>
    {
        private readonly IBus _bus;

        public MyProcessManager(IBus bus)
        {
            _bus = bus;
        }

        protected override void ConfigureHowToFindProcessManager(IProcessManagerPropertyMapper mapper)
        {
            //mapper.ConfigureMapping<MyProcessManagerData, Process1ResponseMessage>(m=>m.PmWidget.Size.Width, pm=>pm.Widget.Size);
            //mapper.ConfigureMapping<MyProcessManagerData, Process1ResponseMessage>(m => m.ProcessId, pm => pm.ProcessId);
            mapper.ConfigureMapping<MyProcessManagerData, Process1ResponseMessage>(m => m.Name, pm => pm.Name);

            mapper.ConfigureMapping<MyProcessManagerData, Process2ResponseMessage>(m => m.PmWidget.Size.Width, pm => pm.Widget.Size);
        }

        public void Execute(StartProcessManagerMessage message)
        {
            Data.CorrelationId = message.CorrelationId;
            Data.Name = "Process_" + message.ProcessId;
            Data.ProcessId = message.ProcessId;
            Data.PmWidget = new PmWidget { Size = new PmWidgetSize { Width = message.ProcessId } };

            Console.WriteLine("MyProcessManager started - {0} ({1})", message.ProcessId, message.CorrelationId);

            _bus.Send("ProcessManager.Process1", new Process1RequestMessage(message.CorrelationId) { ProcessId = message.ProcessId });
        }

        public void Execute(Process1ResponseMessage message)
        {
            Console.WriteLine("Received Process1ResponseMessage: {0} {1}", message.ProcessId, message.Name);

            Data.Name = "UpdatedProcess_" + message.ProcessId;

            _bus.Send("ProcessManager.Process2", new Process2RequestMessage(message.CorrelationId) { ProcessId = message.ProcessId });
        }

        public void Execute(Process2ResponseMessage message)
        {
            Console.WriteLine("Received Process2ResponseMessage: {0} {1}", message.ProcessId, message.Name);

            Complete = true;
        }
    }
}
