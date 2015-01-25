using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ProcessManager.Messages;
using R.MessageBus.Interfaces;

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
        public int Age { get; set; }
        public PmWidget PmWidget { get; set; }
    }

    public class MyProcessManager : R.MessageBus.Core.ProcessManager<MyProcessManagerData>,
        IStartProcessManager<StartProcessManagerMessage>,
        IMessageHandler<Process1ResponseMessage>
    {
        private readonly IBus _bus;

        public MyProcessManager(IBus bus)
        {
            _bus = bus;
        }

        protected override void ConfigureHowToFindProcessManager(R.MessageBus.Interfaces.ProcessManagerPropertyMapper mapper)
        {
            mapper.ConfigureMapping<MyProcessManagerData, Process1ResponseMessage>(m=>m.PmWidget.Size.Width, pm=>pm.Widget.Size);
        }

        public void Execute(StartProcessManagerMessage message)
        {
            Data.CorrelationId = Guid.NewGuid();
            Data.Name = "Name1";
            Data.Age = 1;
            Data.PmWidget = new PmWidget { Size = new PmWidgetSize {Width = 1}};

            Console.WriteLine("MyProcessManager started - {0}", message.CorrelationId);

            Console.WriteLine("Sending Process1RequestMessage");

            _bus.Send("ProcessManager.Process1", new Process1RequestMessage(Guid.NewGuid()));
        }

        public void Execute(Process1ResponseMessage message)
        {
            Console.WriteLine("Received Process1ResponseMessage");

            Complete = true;
        }
    }
}
