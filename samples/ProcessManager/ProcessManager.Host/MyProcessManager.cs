using System;
using ProcessManager.Messages;
using ServiceConnect.Interfaces;

namespace ProcessManager.Host
{

    public class MyProcessManagerData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public bool Process1ResponseMessage { get; set; }
        public bool Process2ResponseMessage { get; set; }
        public int Count { get; set; }
        public int Total { get; set; }
    }

    public class MyProcessManager : ServiceConnect.Core.ProcessManager<MyProcessManagerData>,
        IStartProcessManager<StartProcessManagerMessage>,
        IMessageHandler<Process1ResponseMessage>,
        IMessageHandler<Process2ResponseMessage>
    {
        private readonly IBus _bus;

        public MyProcessManager(IBus bus)
        {
            _bus = bus;
        }

        public void Execute(StartProcessManagerMessage message)
        {
            Data.CorrelationId = message.CorrelationId;

            Data.Total = 1000;
            Data.Count = 0;
            for (int i = 0; i < Data.Total; i++)
            {
                _bus.Send("ProcessManager.Process1", new Process1RequestMessage(message.CorrelationId));
                _bus.Send("ProcessManager.Process2", new Process2RequestMessage(message.CorrelationId));
            }
        }

        public void Execute(Process1ResponseMessage message)
        {
            Data.Count++;
            if (Data.Count == (Data.Total * 2))
            {
                _bus.Send("ProcessManager.Client", new ProcessManagerFinishedMessage(message.CorrelationId));
                MarkAsComplete();
            }
        }

        public void Execute(Process2ResponseMessage message)
        {
            Data.Count++;
            if (Data.Count == (Data.Total * 2))
            {
                _bus.Send("ProcessManager.Client", new ProcessManagerFinishedMessage(message.CorrelationId));
                MarkAsComplete();
            }
        }
    }
}
