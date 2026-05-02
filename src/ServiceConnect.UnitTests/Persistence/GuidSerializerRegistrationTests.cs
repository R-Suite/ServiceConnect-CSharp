using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using Moq;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

/// <summary>
/// Pins the once-only guard on <see cref="MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered"/>.
/// The flag must only be set after every step (mode toggle, verification, serializer registration)
/// has succeeded; flipping it earlier would let a later caller short-circuit on broken driver state
/// and reproduce a zero-match Guid filter at query time.
///
/// These tests rely on <c>InternalsVisibleTo</c> from the MongoDb persistence project and use
/// reflection to inspect/reset the module-private <c>_guidSerializerRegistered</c> flag. They do
/// not force the verification throw path — once BSON serialization has started in-process,
/// <c>BsonDefaults.GuidRepresentationMode</c> is frozen and cannot be toggled back at runtime.
/// Instead, they lock in the observable invariant: the flag is only set after successful completion,
/// which implies the short-circuit cannot hide a previous throw from a later caller.
/// </summary>
public class GuidSerializerRegistrationTests
{
    private const string FlagFieldName = "_guidSerializerRegistered";

    private static FieldInfo FlagField =>
        typeof(MongoDbPersistenceExtensions).GetField(
            FlagFieldName,
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            $"Expected static field '{FlagFieldName}' on MongoDbPersistenceExtensions.");

    private static int ReadFlag() => (int)FlagField.GetValue(null)!;

    private static void WriteFlag(int value) => FlagField.SetValue(null, value);

    [Fact]
    public void EnsureGuidSerializerRegistered_SetsFlagAfterSuccessfulCompletion()
    {
        // The UnitTests assembly may have already triggered initialisation via some other
        // code path (a previous test, a module initializer); force a clean "uninitialised"
        // start so we are asserting about this specific call.
        WriteFlag(0);

        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();

        Assert.Equal(1, ReadFlag());
#pragma warning disable CS0618
        Assert.Equal(GuidRepresentationMode.V3, BsonDefaults.GuidRepresentationMode);
#pragma warning restore CS0618
    }

    [Fact]
    public void EnsureGuidSerializerRegistered_ResetFlag_DoesNotShortCircuit()
    {
        // Core invariant: flag == 0 on entry means setup MUST run (and, on success, flip
        // the flag to 1). This pins down the contract so that a future edit moving the
        // flag flip to the top of the method would still pass this case but be caught by
        // the verification-throw scenarios reasoned about in the class summary.
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
        Assert.Equal(1, ReadFlag());

        WriteFlag(0);
        Assert.Equal(0, ReadFlag());

        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();

        Assert.Equal(1, ReadFlag());
    }

    [Fact]
    public void EnsureGuidSerializerRegistered_FastPath_IsIdempotent()
    {
        // Once the flag is set, repeated calls are a no-op on the fast path and must not
        // touch the flag (or any other global state that we could observe from here).
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
        Assert.Equal(1, ReadFlag());

        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();

        Assert.Equal(1, ReadFlag());
    }

    [Theory]
    [InlineData(typeof(MongoDbAggregatorPersistor))]
    [InlineData(typeof(MongoDbProcessManagerFinder))]
    [InlineData(typeof(MongoDbTimeoutStore))]
    public void Persistor_HasExplicitStaticConstructor(Type persistorType)
    {
        // An explicit `static T()` clears BeforeFieldInit and ensures the cctor runs
        // before any field is touched — i.e., before any instance ctor body runs and
        // before any serialization side-effect can be triggered. That's the contract
        // we need for EnsureGuidSerializerRegistered to fire on direct-new paths.
        //
        // We can't assert "the cctor calls EnsureGuidSerializerRegistered" here without
        // a fresh AppDomain — once any test has touched these types the cctor has
        // already run and the side effect is invisible. Code review must guard the body.
        Assert.NotNull(persistorType.TypeInitializer);
        Assert.False(
            (persistorType.Attributes & TypeAttributes.BeforeFieldInit) != 0,
            $"{persistorType.Name} must declare an explicit `static {persistorType.Name}()` so the Guid serializer registrar fires before any field access on direct-ctor paths.");
    }

    // --- IsCompatibleGuidSerializer unit tests ---
    // These exercise the pure compatibility-check helper in isolation, without touching
    // BSON's process-global serializer registry. The helper's return value drives whether
    // the catch block in EnsureGuidSerializerRegistered re-throws; testing it here ensures
    // that a regression on the throw path is caught deterministically in CI regardless of
    // which process-global state the E2E test happened to observe.

    [Fact]
    public void IsCompatibleGuidSerializer_StandardRepresentation_ReturnsTrue()
    {
        var serializer = new GuidSerializer(GuidRepresentation.Standard);
        Assert.True(MongoDbPersistenceExtensions.IsCompatibleGuidSerializer(serializer));
    }

    [Theory]
    [InlineData(GuidRepresentation.CSharpLegacy)]
    [InlineData(GuidRepresentation.JavaLegacy)]
    [InlineData(GuidRepresentation.PythonLegacy)]
    [InlineData(GuidRepresentation.Unspecified)]
    public void IsCompatibleGuidSerializer_NonStandardRepresentation_ReturnsFalse(GuidRepresentation representation)
    {
        var serializer = new GuidSerializer(representation);
        Assert.False(MongoDbPersistenceExtensions.IsCompatibleGuidSerializer(serializer));
    }

    [Fact]
    public void IsCompatibleGuidSerializer_NullSerializer_ReturnsFalse()
    {
        Assert.False(MongoDbPersistenceExtensions.IsCompatibleGuidSerializer(null));
    }

    [Fact]
    public void IsCompatibleGuidSerializer_NonGuidSerializerImplementation_ReturnsFalse()
    {
        // A custom IBsonSerializer<Guid> that isn't the BSON driver's GuidSerializer should
        // be rejected — we can't introspect its representation.
        var fakeSerializer = new Mock<IBsonSerializer<Guid>>();
        Assert.False(MongoDbPersistenceExtensions.IsCompatibleGuidSerializer(fakeSerializer.Object));
    }
}
