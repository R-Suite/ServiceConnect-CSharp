using ServiceConnect.Examples.ScatterGather.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.ScatterGather.CatalogA;

public sealed class SearchRequestHandler : IMessageHandler<SearchRequest>
{
    public async Task HandleAsync(SearchRequest message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        await context.ReplyAsync(new SearchResponse(message.CorrelationId)
        {
            CatalogName = "CatalogA",
            ResultId = "catalog-a-result-001"
        });

        ConsoleStatus.Success("scatter-gather-catalog-a", $"returned CatalogA/catalog-a-result-001 for {message.Query}");
    }
}
