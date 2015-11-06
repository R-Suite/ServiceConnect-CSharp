namespace ServiceConnect.Interfaces
{
    public interface IFilter
    {
        bool Process(Envelope envelope);
    }
}