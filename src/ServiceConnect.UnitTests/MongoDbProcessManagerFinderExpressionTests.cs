using System.Linq.Expressions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

// Expression.Convert for property-hierarchy queries.
//
// A dynamic predicate that uses Expression.Constant(value, value.GetType()) on the
// RHS — i.e., the runtime type of the message value — silently misses stored documents
// whenever the saga-side property is declared as a wider/different type (long vs
// message-side int, Nullable<T> vs T, interface vs concrete): the resulting BSON
// filter renders against the runtime type rather than the declared type. The InMemory
// finder wraps the RHS in Expression.Convert(.. , declaredPropertyType); these tests
// pin the equivalent wrapping into MongoDb's expression-tree shape so future drift is
// caught at the structural level.
[Collection("Mongo Bson serial")]
public class MongoDbProcessManagerFinderExpressionTests
{
    static MongoDbProcessManagerFinderExpressionTests()
    {
        // The finder's static ctor calls this on first construction, but pin it
        // explicitly so test ordering inside the assembly doesn't matter.
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    public sealed class TestSagaData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public long LongProp { get; set; }
        public int? NullableIntProp { get; set; }
    }

    public sealed class TestMessage(Guid correlationId) : Message(correlationId)
    {
        // int on the message side, long on the saga side — the type-mismatch case where
        // the expression-tree's RHS must be Convert(Constant(int), long) rather than a
        // bare Constant(int) so the MongoDB driver can match the saga's long-typed field.
        public int LongProp { get; set; }
        public int NullableIntProp { get; set; }
    }

    private sealed class InlineMapper : IProcessManagerPropertyMapper
    {
        private readonly List<ProcessManagerToMessageMap> _mappings = [];
        public IReadOnlyList<ProcessManagerToMessageMap> Mappings => _mappings;

        public void Add(string sagaPropertyName, Type declaredType, Type messageType, Func<object, object> messageProp)
        {
            _mappings.Add(new ProcessManagerToMessageMap
            {
                MessageType = messageType,
                MessageProp = messageProp,
                PropertiesHierarchy = new Dictionary<string, Type>(StringComparer.Ordinal)
                {
                    [sagaPropertyName] = declaredType
                }
            });
        }

        public void ConfigureMapping<TProcessManagerData, TMessage>(
            Expression<Func<TProcessManagerData, object>> processManagerProperty,
            Expression<Func<TMessage, object>> messageExpression)
            where TProcessManagerData : IProcessManagerData
            where TMessage : Message
        {
            // Not used by these tests — the inline Add method is enough.
            throw new NotSupportedException();
        }
    }

    private static (MongoDbProcessManagerFinder Finder, Mock<IMongoCollection<MongoDbData<TestSagaData>>> Collection)
        BuildFinder()
    {
        var indexes = new Mock<IMongoIndexManager<MongoDbData<TestSagaData>>>();
        indexes.Setup(m => m.CreateOneAsync(
                It.IsAny<CreateIndexModel<MongoDbData<TestSagaData>>>(),
                It.IsAny<CreateOneIndexOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("ok");

        var collection = new Mock<IMongoCollection<MongoDbData<TestSagaData>>>();
        collection.SetupGet(c => c.Indexes).Returns(indexes.Object);

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<MongoDbData<TestSagaData>>(It.IsAny<string>(), null))
            .Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.W1 });
        client.Setup(c => c.GetDatabase(It.IsAny<string>(), null)).Returns(database.Object);

        var finder = new MongoDbProcessManagerFinder(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            NullLogger<MongoDbProcessManagerFinder>.Instance);

        return (finder, collection);
    }

    private static (MongoDbProcessManagerFinder Finder, Func<Expression<Func<MongoDbData<TestSagaData>, bool>>> Capture)
        BuildFinderAndCapture()
    {
        var (finder, collection) = BuildFinder();

        FilterDefinition<MongoDbData<TestSagaData>>? captured = null;
        var emptyCursor = new Mock<IAsyncCursor<MongoDbData<TestSagaData>>>();
        emptyCursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        emptyCursor.SetupGet(c => c.Current).Returns([]);

        // The Find(Expression).FirstOrDefaultAsync chain bottoms out in
        // IMongoCollection.FindAsync(FilterDefinition, FindOptions, ct) on this
        // collection. Capture the FilterDefinition there.
        collection.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<MongoDbData<TestSagaData>>>(),
                It.IsAny<FindOptions<MongoDbData<TestSagaData>, MongoDbData<TestSagaData>>>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<MongoDbData<TestSagaData>>, FindOptions<MongoDbData<TestSagaData>, MongoDbData<TestSagaData>>, CancellationToken>(
                (f, _, _) => captured = f)
            .ReturnsAsync(emptyCursor.Object);

        return (finder, () =>
        {
            Assert.NotNull(captured);
            // The Find(Expression) extension wraps the lambda in an
            // ExpressionFilterDefinition<T>, which exposes the original
            // expression via the public Expression property.
            var exprFilter = Assert.IsType<ExpressionFilterDefinition<MongoDbData<TestSagaData>>>(captured!);
            return exprFilter.Expression;
        });
    }

    [Fact]
    public async Task FindDataAsync_MessagePropIntSagaPropLong_ExpressionContainsConvertToLong()
    {
        var (finder, capture) = BuildFinderAndCapture();

        var mapper = new InlineMapper();
        mapper.Add(
            sagaPropertyName: nameof(TestSagaData.LongProp),
            declaredType: typeof(long),
            messageType: typeof(TestMessage),
            messageProp: m => ((TestMessage)m).LongProp);

        var msg = new TestMessage(Guid.NewGuid()) { LongProp = 42 };
        await finder.FindDataAsync<TestSagaData>(mapper, msg);

        var expr = capture();
        var equal = Assert.IsAssignableFrom<BinaryExpression>(expr.Body);
        // equal.Right must be a UnaryExpression(Convert, type=long) wrapping the int
        // constant — a bare ConstantExpression of type int would not match the saga's
        // long-typed field via the MongoDB driver's expression translation.
        var convert = Assert.IsAssignableFrom<UnaryExpression>(equal.Right);
        Assert.Equal(ExpressionType.Convert, convert.NodeType);
        Assert.Equal(typeof(long), convert.Type);
        var inner = Assert.IsAssignableFrom<ConstantExpression>(convert.Operand);
        Assert.Equal(42, inner.Value);
        Assert.Equal(typeof(int), inner.Type);
    }

    [Fact]
    public async Task FindDataAsync_NullableSagaProperty_ExpressionContainsConvertToNullable()
    {
        var (finder, capture) = BuildFinderAndCapture();

        var mapper = new InlineMapper();
        mapper.Add(
            sagaPropertyName: nameof(TestSagaData.NullableIntProp),
            declaredType: typeof(int?),
            messageType: typeof(TestMessage),
            messageProp: m => ((TestMessage)m).NullableIntProp);

        var msg = new TestMessage(Guid.NewGuid()) { NullableIntProp = 7 };
        await finder.FindDataAsync<TestSagaData>(mapper, msg);

        var expr = capture();
        var equal = Assert.IsAssignableFrom<BinaryExpression>(expr.Body);
        // equal.Right must be Convert(Constant(int), int?) so the comparison lifts to
        // the saga's nullable-int field type.
        var convert = Assert.IsAssignableFrom<UnaryExpression>(equal.Right);
        Assert.Equal(ExpressionType.Convert, convert.NodeType);
        Assert.Equal(typeof(int?), convert.Type);
    }
}
