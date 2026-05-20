using ServiceConnect.Examples.StressHarness.Chaos;

namespace ServiceConnect.Examples.StressHarness.Assertions;

/// <summary>
/// Pure function from <see cref="LedgerSnapshot"/> to <see cref="MessageLedgerAnalysis"/>.
/// Classifies each publish row into one of four quadrants by joining against the consume
/// rows on <see cref="PublishRecord.MessageId"/>:
/// <list type="bullet">
///   <item>Acked + ≥1 consume → normal delivery</item>
///   <item>Acked + 0 consumes → <c>AckedButLost</c> (hypothesis H1)</item>
///   <item>Failed + ≥1 consume → <c>FailedThenConsumed</c> (probably client retry)</item>
///   <item>Failed + 0 consumes → <c>FailedAndLost</c> (expected when broker is down)</item>
/// </list>
/// Also surfaces per-message redelivery counts (extras beyond the first consume) and a
/// 20-row forensic sample of acked-but-lost publishes for hand-tracing in broker logs.
/// </summary>
public static class MessageLedgerAnalyzer
{
    private const int ForensicSampleSize = 20;

    public static MessageLedgerAnalysis Analyze(LedgerSnapshot snapshot)
    {
        var consumesByMessage = snapshot.Consumes
            .GroupBy(c => c.MessageId)
            .ToDictionary(g => g.Key, g => g.Count());

        var ackedAndConsumed = 0;
        var ackedButLost = 0;
        var failedThenConsumed = 0;
        var failedAndLost = 0;
        var ackedPublishes = 0;
        var failedPublishes = 0;
        var perMessageRedeliveries = 0;

        var byWindow = new Dictionary<ChaosWindow, int>();
        var byPattern = new Dictionary<string, int>(StringComparer.Ordinal);
        var ackedButLostRows = new List<PublishRecord>();

        var publishedMessageIds = new HashSet<Guid>();

        foreach (var publish in snapshot.Publishes)
        {
            publishedMessageIds.Add(publish.MessageId);
            var consumeCount = consumesByMessage.GetValueOrDefault(publish.MessageId, 0);

            if (publish.Outcome == PublishOutcome.Acked)
            {
                ackedPublishes++;
                if (consumeCount > 0)
                {
                    ackedAndConsumed++;
                    if (consumeCount > 1)
                    {
                        perMessageRedeliveries += consumeCount - 1;
                    }
                }
                else
                {
                    ackedButLost++;
                    byWindow[publish.Window] = byWindow.GetValueOrDefault(publish.Window, 0) + 1;
                    byPattern[publish.Pattern] = byPattern.GetValueOrDefault(publish.Pattern, 0) + 1;
                    ackedButLostRows.Add(publish);
                }
            }
            else
            {
                failedPublishes++;
                if (consumeCount > 0)
                {
                    failedThenConsumed++;
                    if (consumeCount > 1)
                    {
                        perMessageRedeliveries += consumeCount - 1;
                    }
                }
                else
                {
                    failedAndLost++;
                }
            }
        }

        var consumesWithoutPublish = 0;
        foreach (var (messageId, count) in consumesByMessage)
        {
            if (!publishedMessageIds.Contains(messageId))
            {
                consumesWithoutPublish += count;
            }
        }

        var sample = ackedButLostRows
            .OrderBy(r => r.PublishStarted)
            .Take(ForensicSampleSize)
            .ToArray();

        return new MessageLedgerAnalysis(
            TotalPublishes: snapshot.Publishes.Count,
            AckedPublishes: ackedPublishes,
            FailedPublishes: failedPublishes,
            TotalConsumes: snapshot.Consumes.Count,
            AckedAndConsumed: ackedAndConsumed,
            AckedButLost: ackedButLost,
            FailedThenConsumed: failedThenConsumed,
            FailedAndLost: failedAndLost,
            PerMessageRedeliveries: perMessageRedeliveries,
            AckedButLostByWindow: byWindow,
            AckedButLostByPattern: byPattern,
            AckedButLostSample: sample,
            ConsumesWithoutPublish: consumesWithoutPublish);
    }
}
