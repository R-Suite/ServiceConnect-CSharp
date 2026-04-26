using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class CountingProcessManagerData : IProcessManagerData
    {
        public static int GetterCount;

        public Guid CorrelationId { get; set; }

        private string _name = "";
        public string Name
        {
            get { System.Threading.Interlocked.Increment(ref GetterCount); return _name; }
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
                if (i == 42) targetId = id;
                await finder.InsertDataAsync(
                    new CountingProcessManagerData { CorrelationId = id, Name = $"item-{i}" },
                    CancellationToken.None);
            }

            var mapper = new TestProcessManagerPropertyMapper();
            mapper.ConfigureMapping<IProcessManagerData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);

            // Inserts deep-clone on store (one getter visit per insert via JSON serialization).
            // Reset so we measure only the work performed by FindDataAsync.
            CountingProcessManagerData.GetterCount = 0;

            var found = await finder.FindDataAsync<IProcessManagerData>(
                mapper, new Message(targetId), CancellationToken.None);

            Assert.NotNull(found);
            Assert.Equal(targetId, ((CountingProcessManagerData)found!.Data).CorrelationId);

            // Contract: FindDataAsync must not clone every candidate. With the bug, the
            // counter reaches the partition size (one JSON serialization per candidate).
            // After the fix, only the matched item is cloned, so the counter stays small.
            Assert.True(
                CountingProcessManagerData.GetterCount <= 5,
                $"Expected at most a handful of getter reads (clone of matched item only), got {CountingProcessManagerData.GetterCount}");
        }
    }
}
