namespace R.MessageBus.Interfaces
{
    public interface IPersistanceData<T> 
    {
        T Data { get; set; }
    }
}