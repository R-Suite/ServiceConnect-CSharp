using ServiceConnect.Examples.RequestReply.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.RequestReply.Responder;

public sealed class QuoteRequestHandler : IMessageHandler<QuoteRequest>
{
    public async Task HandleAsync(QuoteRequest message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        await context.ReplyAsync(new QuoteResponse(message.CorrelationId) { Price = 42.50m });
        ConsoleStatus.Success("request-reply-responder", $"processed {message.ProductCode}");
    }
}
