using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// xUnit collection for tests that touch the global BSON serializer registry indirectly via
/// <c>MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered</c> — i.e., any test that
/// constructs <c>MongoClientFactory</c>, <c>MongoDbAggregatorPersistor</c>,
/// <c>MongoDbProcessManagerFinder</c>, <c>MongoDbTimeoutStore</c>, or invokes the registrar
/// directly. The once-only guard test in <c>GuidSerializerRegistrationTests</c> resets the
/// module-private <c>_guidSerializerRegistered</c> flag via reflection to exercise the
/// short-circuit contract; while the flag is briefly zero, sibling tests racing through
/// <c>BsonSerializer.RegisterSerializer</c> on a parallel thread observe a registered serializer
/// and throw <c>BsonSerializationException</c>. Forcing serial execution within this collection
/// keeps the registry quiet for the duration of the reset/re-register cycle. Non-Mongo tests
/// continue to parallelize freely.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MongoBsonSerialCollection
{
    public const string Name = "Mongo Bson serial";
}
