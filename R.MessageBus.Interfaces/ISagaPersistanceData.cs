namespace R.MessageBus.Interfaces
{
    public interface ISagaPersistanceData<T>
    {
         T Data { get; set; }
    }
}