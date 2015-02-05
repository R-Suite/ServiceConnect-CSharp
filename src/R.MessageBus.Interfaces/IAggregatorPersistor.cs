using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public interface IAggregatorPersistor
    {
        void InsertData(object data, string name);
        IList<object> GetData(string name);
        void RemoveAll(string name);
        int Count(string name);
    }
}
