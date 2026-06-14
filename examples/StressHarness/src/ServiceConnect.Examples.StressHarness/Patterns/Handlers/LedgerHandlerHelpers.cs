using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

internal static class LedgerHandlerHelpers
{
    /// <summary>
    /// Records a consume row in <see cref="MessageLedger"/> for the inbound message.
    /// Reads <see cref="StressHeaders.MessageId"/> from <paramref name="context"/> headers
    /// when present; falls back to <see cref="Message.CorrelationId"/> for patterns whose
    /// publish path does not carry caller-controlled headers (routing-slip via
    /// <see cref="IBus.RouteAsync"/>). The publish side records under the same fallback
    /// key for those paths so consume rows pair correctly.
    /// </summary>
    public static void RecordLedgerConsume(
        Message message,
        IConsumeContext context,
        string pattern,
        string busTag,
        Guid flowId,
        MessageLedger ledger,
        IChaosClock chaosClock)
    {
        var messageId = message.CorrelationId;
        if (context.Headers.TryGetValue(StressHeaders.MessageId, out var raw)
            && HeaderDecoder.Decode(raw) is { } msgIdStr
            && Guid.TryParseExact(msgIdStr, "N", out var parsed))
        {
            messageId = parsed;
        }
        ledger.RecordConsume(messageId, flowId, pattern, busTag, DateTimeOffset.UtcNow, chaosClock.CurrentWindow);
    }
}
