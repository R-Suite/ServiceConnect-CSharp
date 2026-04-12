namespace ServiceConnect.Interfaces;

public interface IProcessHandler<TData, TMessage>
    where TData : class, IProcessManagerData, new()
    where TMessage : Message
{
    IConsumeContext? Context { get; set; }
    Task HandleAsync(TMessage message, TData data);

    void ConfigureMapper(IProcessManagerPropertyMapper mapper)
    {
        mapper.ConfigureMapping<TData, TMessage>(d => d.CorrelationId, m => m.CorrelationId);
    }
}
