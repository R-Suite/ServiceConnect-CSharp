using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Request half of the stress harness's scatter-gather pattern. The driver
/// publishes one of these via <see cref="ServiceConnect.Interfaces.IBus.PublishRequestAsync"/>,
/// which fans the message out across every bus subscribed to <see cref="SearchRequest"/>'s
/// type-fanout exchange — both alpha and beta receive one delivery, each handler
/// replies through <see cref="ServiceConnect.Interfaces.IConsumeContext.ReplyAsync"/>,
/// and the driver counts the assembled replies against <c>ExpectedReplyCount = 2</c>.
/// </summary>
public sealed class SearchRequest(Guid correlationId) : Message(correlationId)
{
    /// <summary>
    /// Free-text query field echoed in the response. Driver sets it to the flow id so
    /// the payload remains correlatable end-to-end alongside the framework's
    /// request-message-id header machinery.
    /// </summary>
    public string Query { get; init; } = string.Empty;
}
