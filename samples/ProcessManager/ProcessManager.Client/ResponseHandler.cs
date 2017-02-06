using System;
using ProcessManager.Client;
using ProcessManager.Messages;
using ServiceConnect.Interfaces;

namespace ProcessManager.Process2
{
    public class ProcessManagerFinishedHandler : IMessageHandler<ProcessManagerFinishedMessage>
    {
        static int count;

        public void Execute(ProcessManagerFinishedMessage message)
        {
            count++;
            if (count == State.Pms)
            {
                count = 0;
                Console.WriteLine("{0} - {1}", State.Pms, (DateTime.UtcNow - State.Start).TotalMilliseconds);
                State.Finished = true;
            }
        }

        public IConsumeContext Context { get; set; }
    }
}
