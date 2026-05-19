using System.Collections.Concurrent;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Process-wide hit counter for the competing-consumers driver. Each
/// <see cref="WorkItemHandler"/> instance records into a key of the form
/// <c>"{busTag}:{handlerTag}"</c> so the driver can verify that more than one
/// distinct handler observed at least one message after the batch drains.
/// </summary>
/// <remarks>
/// A single instance is shared across both buses (registered as a singleton in
/// <c>Program.cs</c>), mirroring the <c>FlowAccounting</c> and
/// <see cref="PerHandlerSignal"/> sharing model. Concurrent updates are bounded
/// by the number of dispatch threads on both buses; the dictionary's lock-free
/// reads keep the driver's polling reconcile cheap.
/// </remarks>
public sealed class WorkItemCounters
{
    /// <summary>
    /// Per-handler hit counts keyed by <c>"{busTag}:{handlerTag}"</c>. Composite key
    /// keeps the alpha-side and beta-side counters in one shared dictionary while
    /// remaining unambiguous when the driver inspects only the receiver-side bus tag.
    /// </summary>
    public ConcurrentDictionary<string, int> Hits { get; } = new(StringComparer.Ordinal);
}
