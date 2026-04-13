namespace ServiceConnect.Interfaces;

public interface IConsumeContext
{
    IBus Bus { get; }
    IDictionary<string, object> Headers { get; }
    string? MessageId { get; }
    Guid CorrelationId { get; }
    CancellationToken CancellationToken { get; set; }
    Task ReplyAsync<TReply>(TReply message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where TReply : Message;
}
