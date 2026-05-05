using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class InMemoryProcessManagerFinderInterfacePropertyTests
{
    // The interface whose property is implemented explicitly on ExplicitSagaData.
    public interface IFooSource
    {
        Guid FooId { get; }
    }

    // Saga data with an explicit-interface property for FooId.
    // FooIdValue is a public serialisation-visible backing property so the deep-clone
    // round-trip preserves the value; FooId is only reachable via IFooSource, making
    // the string-name lookup on the runtime type inadequate without the interface walk.
    public sealed class ExplicitSagaData : IProcessManagerData, IFooSource
    {
        public Guid CorrelationId { get; set; }

        // Public so Newtonsoft.Json can round-trip the value through DeepClone.Clone.
        public Guid FooIdValue { get; set; }

        // Explicit-interface impl: not reachable via ExplicitSagaData.GetProperty("FooId").
        // The expression tree builder must find IFooSource.FooId via the interface walk.
        Guid IFooSource.FooId => FooIdValue;
    }

    // Minimal message whose FooId is the correlation key.
    public sealed class FooMessage(Guid fooId) : Message(fooId)
    {
        public Guid FooId => CorrelationId;
    }

    [Fact]
    public async Task FindData_ExplicitInterfaceImpl_BuildsCorrectPredicate()
    {
        // GetPredicate must build the member access via MakeMemberAccess with the
        // IFooSource.FooId PropertyInfo, so the lookup resolves through the declaring
        // interface type. Resolving by string name on the saga's runtime type
        // (Expression.Property(left, left.Type, "FooId")) would miss explicit-interface
        // implementations and throw ArgumentException.

        var fooId = Guid.NewGuid();
        var data = new ExplicitSagaData
        {
            CorrelationId = Guid.NewGuid(),
            FooIdValue = fooId
        };

        var mapper = new TestProcessManagerPropertyMapper();
        // The cast to IFooSource in the lambda causes ConfigureMapping to extract the
        // IFooSource.FooId PropertyInfo (Name = "FooId").  GetPredicate<T> must then
        // find that PropertyInfo on ExplicitSagaData via its implemented interfaces,
        // not just by looking up a public property by string name on the runtime type.
        mapper.ConfigureMapping<ExplicitSagaData, FooMessage>(
            d => ((IFooSource)d).FooId,
            m => m.FooId);

        IProcessManagerFinder finder = new InMemoryProcessManagerFinder(new ProcessManagerPredicateCache(), new InMemoryPersistenceState(TimeProvider.System));
        await finder.InsertDataAsync(data, CancellationToken.None);

        var result = await finder.FindDataAsync<ExplicitSagaData>(
            mapper, new FooMessage(fooId), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(data.CorrelationId, result.Data.CorrelationId);
    }
}
