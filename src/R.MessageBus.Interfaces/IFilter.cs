namespace R.MessageBus.Interfaces
{
    public interface IFilter
    {
        bool Process(Envelope envelope);
    }
}