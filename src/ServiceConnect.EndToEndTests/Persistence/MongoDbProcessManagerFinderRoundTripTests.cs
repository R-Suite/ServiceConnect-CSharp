using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson.Serialization;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

// H10 — round-trip exercise of property-hierarchy lookups against a real MongoDB.
//
// The companion unit tests pin the expression-tree shape (RHS wrapped in
// Expression.Convert(.., declaredType)). These tests prove the same fix works
// end-to-end against the BSON serializer + driver: a saga inserted with a
// wider/different declared type must be findable by a message that exposes the
// matching property at a narrower runtime type.
[Collection(nameof(PersistenceCollection))]
public class MongoDbProcessManagerFinderRoundTripTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    static MongoDbProcessManagerFinderRoundTripTests()
    {
        // BSON ClassMap registrations must be in place before the first read/write
        // of each saga type so AutoMap pins the (Standard) Guid serializer for the
        // CorrelationId property — the same defensive registration the sibling
        // suites use. IsClassMapRegistered guards against duplicate registration
        // when this assembly is loaded multiple times.
        if (!BsonClassMap.IsClassMapRegistered(typeof(LongPropSagaData)))
        {
            BsonClassMap.RegisterClassMap<LongPropSagaData>(cm =>
            {
                cm.AutoMap();
                cm.SetIsRootClass(true);
            });
        }
        if (!BsonClassMap.IsClassMapRegistered(typeof(NullableIntPropSagaData)))
        {
            BsonClassMap.RegisterClassMap<NullableIntPropSagaData>(cm =>
            {
                cm.AutoMap();
                cm.SetIsRootClass(true);
            });
        }
        if (!BsonClassMap.IsClassMapRegistered(typeof(DecimalPropSagaData)))
        {
            BsonClassMap.RegisterClassMap<DecimalPropSagaData>(cm =>
            {
                cm.AutoMap();
                cm.SetIsRootClass(true);
            });
        }
    }

    public class LongPropSagaData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public long OrderNumber { get; set; }
    }

    public class NullableIntPropSagaData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public int? Sequence { get; set; }
    }

    public class DecimalPropSagaData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public decimal Amount { get; set; }
    }

    public class IntPropMessage(Guid correlationId) : Message(correlationId)
    {
        public int OrderNumber { get; set; }
        public int Sequence { get; set; }
        public int Amount { get; set; }
    }

    private MongoDbProcessManagerFinder CreateFinder(string prefix)
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = _fixture.GetUniqueDatabaseName(prefix),
        };
        var client = MongoClientFactory.Create(options);
        return new MongoDbProcessManagerFinder(client, options, NullLogger<MongoDbProcessManagerFinder>.Instance);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task FindData_SagaPropertyLong_MessagePropertyInt_FindsRow()
    {
        // Saga side declared as long, message side declared as int. Pre-fix the
        // dynamic predicate's RHS uses the runtime int type so the BSON projection
        // queries the wrong path / fails to bind, and the find silently misses.
        // Post-fix the Convert(constant, long) coerces both sides to the saga's
        // declared type and the document is found.
        var finder = CreateFinder("pmf_h10_long");
        var corrId = Guid.NewGuid();
        const long orderNumber = 12345L;

        await finder.InsertDataAsync(new LongPropSagaData { CorrelationId = corrId, OrderNumber = orderNumber });

        var mapper = new TestProcessManagerPropertyMapper();
        mapper.ConfigureMapping<LongPropSagaData, IntPropMessage>(
            saga => saga.OrderNumber,
            msg => msg.OrderNumber);

        var msg = new IntPropMessage(corrId) { OrderNumber = (int)orderNumber };
        var result = await finder.FindDataAsync<LongPropSagaData>(mapper, msg);

        Assert.NotNull(result);
        Assert.Equal(corrId, result!.Data.CorrelationId);
        Assert.Equal(orderNumber, result.Data.OrderNumber);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task FindData_SagaPropertyNullableInt_MessagePropertyInt_FindsRow()
    {
        // Saga side is Nullable<int>, message side is plain int. Without the
        // Convert wrapping, Expression.Equal(Nullable<int>, int) is rejected up
        // front (InvalidOperationException) and FindDataAsync surfaces a
        // PersistenceException. Post-fix the Convert(constant, int?) lifts the
        // RHS to the nullable type and the row is returned.
        var finder = CreateFinder("pmf_h10_nullable");
        var corrId = Guid.NewGuid();
        int? sequence = 99;

        await finder.InsertDataAsync(new NullableIntPropSagaData { CorrelationId = corrId, Sequence = sequence });

        var mapper = new TestProcessManagerPropertyMapper();
        mapper.ConfigureMapping<NullableIntPropSagaData, IntPropMessage>(
            saga => saga.Sequence!,
            msg => msg.Sequence);

        var msg = new IntPropMessage(corrId) { Sequence = sequence!.Value };
        var result = await finder.FindDataAsync<NullableIntPropSagaData>(mapper, msg);

        Assert.NotNull(result);
        Assert.Equal(corrId, result!.Data.CorrelationId);
        Assert.Equal(sequence, result.Data.Sequence);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task FindData_SagaPropertyDecimal_MessagePropertyInt_FindsRow()
    {
        // Saga side declared as decimal, message side declared as int. Same
        // failure mode as the long case — Expression.Equal(decimal, int) is
        // rejected pre-fix because no implicit binary operator exists between
        // the two primitive types in the expression-tree binder.
        var finder = CreateFinder("pmf_h10_decimal");
        var corrId = Guid.NewGuid();
        const decimal amount = 250m;

        await finder.InsertDataAsync(new DecimalPropSagaData { CorrelationId = corrId, Amount = amount });

        var mapper = new TestProcessManagerPropertyMapper();
        mapper.ConfigureMapping<DecimalPropSagaData, IntPropMessage>(
            saga => saga.Amount,
            msg => msg.Amount);

        var msg = new IntPropMessage(corrId) { Amount = (int)amount };
        var result = await finder.FindDataAsync<DecimalPropSagaData>(mapper, msg);

        Assert.NotNull(result);
        Assert.Equal(corrId, result!.Data.CorrelationId);
        Assert.Equal(amount, result.Data.Amount);
    }
}
