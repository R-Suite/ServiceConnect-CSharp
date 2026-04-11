namespace ServiceConnect.Interfaces;

public interface IConsumeContext
{
    IBus Bus { get; set; }
    IDictionary<string, object> Headers { get; set; }
    string? MessageId { get; }
    Guid CorrelationId { get; }
    Task ReplyAsync<TReply>(TReply message, Dictionary<string, string>? headers = null) where TReply : Message;
}
