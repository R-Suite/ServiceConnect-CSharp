namespace ServiceConnect.Interfaces;

public interface IPersistenceData<T> where T : class, IProcessManagerData
{
    T Data { get; set; }
}
