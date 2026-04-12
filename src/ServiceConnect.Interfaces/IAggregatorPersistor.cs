namespace ServiceConnect.Interfaces;

public interface IAggregatorPersistor
{
    void InsertData(object data, string name);
    IList<object> GetData(string name);
    void RemoveData(string name, Guid correlationId);
    int Count(string name);
}

