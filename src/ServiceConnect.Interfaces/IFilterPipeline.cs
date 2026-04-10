namespace ServiceConnect.Interfaces;

public interface IFilterPipeline
{
    bool ExecuteOutgoingFilters(Envelope envelope);
    bool ExecuteBeforeConsumingFilters(Envelope envelope);
    bool ExecuteAfterConsumingFilters(Envelope envelope);
}
