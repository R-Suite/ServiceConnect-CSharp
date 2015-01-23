using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ProcessManager.Messages;
using R.MessageBus.Interfaces;

namespace ProcessManager.Host
{
    public class MyProcessManagerData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public string Name { get; set; }
        public int Age { get; set; }
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

        protected override void ConfigureHowToFindProcessManager(ProcessManagerPropertyMapper mapper)
        {
            mapper.ConfigureMapping<MyProcessManagerData, Process1ResponseMessage>(m=>m.Age, pm=>pm.Age);
        }

        public void Execute(StartProcessManagerMessage message)
        {
            Data.CorrelationId = Guid.NewGuid();
            Data.Name = "Name1";
            Data.Age = 1;

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
