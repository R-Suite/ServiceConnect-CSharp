using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// xUnit collection for tests with inherent timing-sensitivity (Barrier convergence,
/// real-timer fires, bounded-time assertions). Tests in this collection run
/// serially with each other so thread-pool contention from parallel tests does
/// not narrow their timing windows. Other test classes (the bulk of the suite)
/// continue to run in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialConcurrencyCollection
{
    public const string Name = "SerialConcurrency";
}
