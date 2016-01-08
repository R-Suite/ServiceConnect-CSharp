using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BusDisposeTest.Messages;
using log4net;
using ServiceConnect.Interfaces;

namespace BusDisposeTest
{
    public class TestMsgHandler : IMessageHandler<TestMsg>
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public void Execute(TestMsg message)
        {
            Logger.Info("in here");
            string createText = "Hello and Welcome" + Environment.NewLine;
            File.WriteAllText("C:\\GIT\\ServiceConnect\\samples\\BusDisposeTest\\BusDisposeTest\\bin\\Debug\\BusDisposeTest.txt", createText);
        }

        public IConsumeContext Context { get; set; }
    }
}
