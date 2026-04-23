using System.Reflection;
using MongoDB.Bson;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

/// <summary>
/// Regression guard for <see cref="MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered"/>.
/// The earlier implementation flipped a once-only guard at the top of the method, so if the
/// post-toggle verification threw, the flag was already set and the next caller returned
/// silently — building a <c>MongoClient</c> on broken driver state and reproducing the exact
/// zero-match Guid filter bug the verification was meant to prevent. The fix defers the flag
/// flip until after every step (mode toggle, verification, serializer registration) succeeds.
///
/// These tests rely on <c>InternalsVisibleTo</c> from the MongoDb persistence project and
/// use reflection to inspect/reset the module-private <c>_guidSerializerRegistered</c> flag.
/// They do not attempt to force the verification throw path — once BSON serialization has
/// started in-process, <c>BsonDefaults.GuidRepresentationMode</c> is frozen and cannot be
/// toggled back to reproduce the failure case at runtime. Instead, they lock in the
/// observable invariant: the flag is only set after a successful completion, which implies
/// the short-circuit cannot hide a previous throw from a later caller.
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
        // This is the core invariant: if the flag is 0 on entry, the method MUST re-run
        // setup (and, on success, flip the flag back to 1). The old buggy implementation
        // also re-ran setup in this case — but only because flag-0 means "not attempted."
        // The regression we guard against is a future edit that moves the flag flip back
        // to the top of the method, which would still pass this test on the first call
        // but would also cause a thrown-verification call to short-circuit future callers.
        // The complementary guard is the inline comment + code review; this test at least
        // pins down that a flag of 0 always means "setup will run" and never shortcuts.
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
}
