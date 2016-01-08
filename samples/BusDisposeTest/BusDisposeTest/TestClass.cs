using System;

namespace BusDisposeTest
{
    public class TestClass : ITestClass
    {
        public void Do()
        {
            throw new NotImplementedException();
        }
    }

    internal interface ITestClass
    {
        void Do();
    }
}
