using ServiceConnect.Interfaces.Exceptions;
using Xunit;

namespace ServiceConnect.UnitTests;

public class ExceptionShapeTests
{
    // L4 regression: the sealed library exceptions used to rely on primary constructors
    // with optional parameters, which does not satisfy CA1032 and (for TransportException
    // / SerializationException) left `new X(message, innerException)` uncompilable because
    // the middle parameter was a string/Type, not an Exception. These tests pin the
    // standard CA1032 constructor shapes.

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
