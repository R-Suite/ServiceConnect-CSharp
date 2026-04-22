using ServiceConnect.Interfaces.Exceptions;
using Xunit;

namespace ServiceConnect.UnitTests;

public class ExceptionShapeTests
{
    // Each sealed library exception must expose the full CA1032 constructor shape
    // (parameterless, message, message+innerException) so callers can wrap inner
    // causes with `new X(message, inner)` uniformly across the exception surface.

    [Fact]
    public void ConcurrencyException_SupportsStandardConstructors()
    {
        var inner = new InvalidOperationException("boom");

        var empty = new ConcurrencyException();
        var message = new ConcurrencyException("m");
        var wrapped = new ConcurrencyException("m", inner);

        Assert.NotNull(empty);
        Assert.Equal("m", message.Message);
        Assert.Same(inner, wrapped.InnerException);
    }

    [Fact]
    public void PersistenceException_SupportsStandardConstructors()
    {
        var inner = new InvalidOperationException("boom");

        var empty = new PersistenceException();
        var message = new PersistenceException("m");
        var wrapped = new PersistenceException("m", inner);

        Assert.NotNull(empty);
        Assert.Equal("m", message.Message);
        Assert.Same(inner, wrapped.InnerException);
    }

    [Fact]
    public void TransportException_SupportsStandardConstructors()
    {
        var inner = new InvalidOperationException("boom");

        var empty = new TransportException();
        var message = new TransportException("m");
        var wrapped = new TransportException("m", inner);
        var withEndpoint = new TransportException("m", "queue://foo", inner);

        Assert.NotNull(empty);
        Assert.Equal("m", message.Message);
        Assert.Null(wrapped.Endpoint);
        Assert.Same(inner, wrapped.InnerException);
        Assert.Equal("queue://foo", withEndpoint.Endpoint);
    }

    [Fact]
    public void SerializationException_SupportsStandardConstructors()
    {
        var inner = new InvalidOperationException("boom");

        var empty = new SerializationException();
        var message = new SerializationException("m");
        var wrapped = new SerializationException("m", inner);
        var withType = new SerializationException("m", typeof(string), inner);

        Assert.NotNull(empty);
        Assert.Equal("m", message.Message);
        Assert.Null(wrapped.MessageType);
        Assert.Same(inner, wrapped.InnerException);
        Assert.Same(typeof(string), withType.MessageType);
    }
}
