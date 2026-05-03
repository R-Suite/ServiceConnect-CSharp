using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests;

public class CountingProcessManagerData : IProcessManagerData
{
    public static int GetterCount;

    public Guid CorrelationId { get; set; }

    private string _name = "";
    public string Name
    {
        get { Interlocked.Increment(ref GetterCount); return _name; }
        set => _name = value;
    }
}

public class InMemoryProcessManagerFinderCloneCountTests
{
    [Fact]
    public async Task FindDataAsync_ClonesOnlyMatchedItem_NotEveryCandidate()
    {
        IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

        const int partitionSize = 100;
        Guid targetId = Guid.Empty;
        for (int i = 0; i < partitionSize; i++)
        {
            var id = Guid.NewGuid();
            if (i == 42)
            {
                targetId = id;
            }

            await finder.InsertDataAsync(
                new CountingProcessManagerData { CorrelationId = id, Name = $"item-{i}" },
                CancellationToken.None);
        }

        var mapper = new TestProcessManagerPropertyMapper();
        mapper.ConfigureMapping<CountingProcessManagerData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);

        // Inserts deep-clone on store (one getter visit per insert via JSON serialization).
        // Reset so we measure only the work performed by FindDataAsync.
        Interlocked.Exchange(ref CountingProcessManagerData.GetterCount, 0);

        var found = await finder.FindDataAsync<CountingProcessManagerData>(
            mapper, new Message(targetId), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(targetId, found!.Data.CorrelationId);

        // DeepClone.Clone runs exactly once on the matched item; Newtonsoft visits the Name getter
        // once during serialization. The tight bound (≤ 2) makes a partial regression to
        // clone-per-candidate visible — that path would push the count into the dozens.
        Assert.True(
            CountingProcessManagerData.GetterCount <= 2,
            $"Expected at most 2 getter reads (clone of matched item only), got {CountingProcessManagerData.GetterCount}");
    }
}
