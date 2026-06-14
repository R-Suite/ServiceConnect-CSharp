using System;
using ServiceConnect.Interfaces.Exceptions;
using Xunit;

namespace ServiceConnect.UnitTests.Exceptions;

/// <summary>
/// Constructor coverage for <see cref="RequestTimeoutException"/>: the original 2-arg
/// shape stays binary-compatible (PartialReplies defaults to empty), the 3-arg shape
/// preserves the supplied list, and a null partials argument coalesces to empty rather
/// than throwing — the exception is a pure data carrier on a failure path.
/// </summary>
public sealed class RequestTimeoutExceptionTests
{
    [Fact]
    public void Ctor_NoPartials_PartialRepliesIsEmpty()
    {
        var ex = new RequestTimeoutException(Guid.NewGuid(), TimeSpan.FromSeconds(1));

        Assert.NotNull(ex.PartialReplies);
        Assert.Empty(ex.PartialReplies);
    }

    [Fact]
    public void Ctor_WithPartials_PartialRepliesPreserved()
    {
        object reply1 = new();
        object reply2 = new();
        var ex = new RequestTimeoutException(Guid.NewGuid(), TimeSpan.FromSeconds(1), [reply1, reply2]);

        Assert.Equal(2, ex.PartialReplies.Count);
        Assert.Same(reply1, ex.PartialReplies[0]);
        Assert.Same(reply2, ex.PartialReplies[1]);
    }

    [Fact]
    public void Ctor_NullPartials_PartialRepliesIsEmpty()
    {
        var ex = new RequestTimeoutException(Guid.NewGuid(), TimeSpan.FromSeconds(1), partialReplies: null!);

        Assert.NotNull(ex.PartialReplies);
        Assert.Empty(ex.PartialReplies);
    }

    [Fact]
    public void Ctor_PreservesCorrelationIdAndElapsed()
    {
        var id = Guid.NewGuid();
        var elapsed = TimeSpan.FromMilliseconds(123);
        var ex = new RequestTimeoutException(id, elapsed);

        Assert.Equal(id, ex.CorrelationId);
        Assert.Equal(elapsed, ex.Elapsed);
    }
}
