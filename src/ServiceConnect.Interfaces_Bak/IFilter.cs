namespace ServiceConnect.Interfaces
{
    public interface IFilter
    {
        IBus Bus { get; set; }
        bool Process(Envelope envelope);
    }
}