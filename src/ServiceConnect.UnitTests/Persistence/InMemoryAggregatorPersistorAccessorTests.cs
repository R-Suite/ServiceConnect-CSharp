using System;
using System.Threading.Tasks;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

/// <summary>
/// Verifies that <see cref="InMemoryAggregatorPersistor"/> throws an
/// <see cref="InvalidOperationException"/> when the data type stored in a
/// stream does not expose a public <c>Guid CorrelationId</c> property, and
/// that the throw-delegate cached for that type re-fires on every subsequent
/// invocation rather than silently returning null.
/// </summary>
public class InMemoryAggregatorPersistorAccessorTests
{
    /// <summary>
    /// A POCO that has no CorrelationId property — the type that triggers the
    /// negative-accessor path inside <c>GetCorrelationId</c>.
    /// </summary>
    public sealed class NoCorrelationIdType { public int OtherProp { get; set; } }

    /// <summary>
    /// A POCO with a public <c>Guid CorrelationId</c> property but that does
    /// not extend <c>Message</c> — exercises the reflection-based happy path.
    /// </summary>
    public sealed class PlainPocoWithCorrelationId
    {
        public Guid CorrelationId { get; init; }
        public string Label { get; init; } = "";
    }

    [Fact]
    public async Task RemoveData_TypeWithoutCorrelationId_ThrowsInvalidOperationException()
    {
        // Insert is allowed — GetCorrelationId is only called from the matching
        // paths (RemoveDataAsync). The throw surfaces when the caller tries to
        // remove by correlation id and the accessor cannot extract one.
        var persistor = new InMemoryAggregatorPersistor("", "", "", null);
        await persistor.InsertDataAsync(new NoCorrelationIdType { OtherProp = 1 }, "test-name");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            persistor.RemoveDataAsync("test-name", Guid.NewGuid()));

        Assert.Contains(typeof(NoCorrelationIdType).FullName!, ex.Message);
    }

    [Fact]
    public async Task RemoveData_TypeWithoutCorrelationId_ThrowsAgainAfterFirstThrow()
    {
        // Pre-fix: the negative-result delegate cached `static _ => null`. Because
        // GetCorrelationId returned null, the match never fired, and RemoveDataAsync
        // fell through to ConcurrencyException — not InvalidOperationException. The
        // wrong exception type masked the real problem, and a second call behaved
        // identically (cached null forever).
        //
        // Post-fix: a throw-delegate is cached. Every invocation throws
        // InvalidOperationException, giving the caller an actionable diagnosis on
        // the first call and on every subsequent call.
        var persistor = new InMemoryAggregatorPersistor("", "", "", null);
        await persistor.InsertDataAsync(new NoCorrelationIdType { OtherProp = 1 }, "test-name");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            persistor.RemoveDataAsync("test-name", Guid.NewGuid()));

        // A second remove must also throw InvalidOperationException, not
        // ConcurrencyException or a silent no-op.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            persistor.RemoveDataAsync("test-name", Guid.NewGuid()));
    }

    [Fact]
    public async Task RemoveData_PlainPocoWithCorrelationId_MatchesAndRemovesWithoutThrow()
    {
        // Regression guard: a non-Message POCO that exposes a Guid CorrelationId
        // must still resolve via the reflection-based accessor without throwing.
        var corrId = Guid.NewGuid();
        var persistor = new InMemoryAggregatorPersistor("", "", "", null);
        await persistor.InsertDataAsync(
            new PlainPocoWithCorrelationId { CorrelationId = corrId, Label = "hello" },
            "stream");

        // Must not throw — accessor finds the property, match succeeds, row removed.
        await persistor.RemoveDataAsync("stream", corrId);

        var remaining = await persistor.GetDataAsync("stream");
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task RemoveData_PlainPocoWithCorrelationId_NoMatchThrowsConcurrencyException()
    {
        // When the accessor succeeds but no row matches the supplied id, the
        // expected exception is ConcurrencyException (the no-match contract),
        // not InvalidOperationException.
        var persistor = new InMemoryAggregatorPersistor("", "", "", null);
        await persistor.InsertDataAsync(
            new PlainPocoWithCorrelationId { CorrelationId = Guid.NewGuid(), Label = "hello" },
            "stream");

        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            persistor.RemoveDataAsync("stream", Guid.NewGuid()));
    }
}
