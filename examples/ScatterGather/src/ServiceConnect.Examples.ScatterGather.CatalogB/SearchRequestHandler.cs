using ServiceConnect.Examples.ScatterGather.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.ScatterGather.CatalogB;

public sealed class SearchRequestHandler : IMessageHandler<SearchRequest>
{
    public IConsumeContext Context { get; set; } = null!;

    public async Task HandleAsync(SearchRequest message, CancellationToken cancellationToken = default)
    {
        await Context!.ReplyAsync(new SearchResponse(message.CorrelationId)
        {
            CatalogName = "CatalogB",
            ResultId = "catalog-b-result-777"
        });

        ConsoleStatus.Success("scatter-gather-catalog-b", $"returned CatalogB/catalog-b-result-777 for {message.Query}");
    }
}
