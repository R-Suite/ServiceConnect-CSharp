using System;
using System.Buffers;
using Moq;
using Moq.Language.Flow;
using ServiceConnect.Interfaces;

namespace ServiceConnect.UnitTests.Fakes;

/// <summary>
/// Test helpers that re-create the v7 byte[]-returning Serialize<T>(T) shape on top of
/// the v8 IBufferWriter-based interface. IMessageSerializer exposes three methods; many
/// unit tests only care that Serialize was invoked with a given message and that a known
/// body propagates onward. These helpers preserve those semantics without re-writing
/// each test individually.
/// </summary>
internal static class MessageSerializerMockExtensions
{
    /// <summary>
    /// Sets up the Serialize&lt;T&gt;(T, IBufferWriter&lt;byte&gt;) overload so that calling
    /// <see cref="IMessageSerializer.Serialize{T}(T, IBufferWriter{byte})"/> with a matching
    /// message writes the provided <paramref name="bytes"/> into the supplied buffer writer.
    /// </summary>
    public static void SetupSerialize<T>(this Mock<IMessageSerializer> mock, T message, byte[] bytes)
        where T : Message
    {
        mock.Setup(x => x.Serialize<T>(message, It.IsAny<IBufferWriter<byte>>()))
            .Callback<T, IBufferWriter<byte>>((_, bw) => bw.Write(bytes));
    }

    /// <summary>
    /// As <see cref="SetupSerialize{T}(Mock{IMessageSerializer}, T, byte[])"/>, but matches
    /// any message of type <typeparamref name="T"/>.
    /// </summary>
    public static void SetupSerializeAny<T>(this Mock<IMessageSerializer> mock, byte[] bytes)
        where T : Message
    {
        mock.Setup(x => x.Serialize<T>(It.IsAny<T>(), It.IsAny<IBufferWriter<byte>>()))
            .Callback<T, IBufferWriter<byte>>((_, bw) => bw.Write(bytes));
    }

    /// <summary>
    /// Verifies that Serialize&lt;T&gt; was called for the given <paramref name="message"/>
    /// at least once (legacy v7 verification shape).
    /// </summary>
    public static void VerifySerialize<T>(this Mock<IMessageSerializer> mock, T message, Times times)
        where T : Message
    {
        mock.Verify(x => x.Serialize<T>(message, It.IsAny<IBufferWriter<byte>>()), times);
    }

    /// <summary>
    /// As above, accepting the Moq factory-method form (e.g. <c>Times.Once</c> without parens)
    /// for symmetry with the rest of the test suite.
    /// </summary>
    public static void VerifySerialize<T>(this Mock<IMessageSerializer> mock, T message, Func<Times> times)
        where T : Message
    {
        mock.Verify(x => x.Serialize<T>(message, It.IsAny<IBufferWriter<byte>>()), times);
    }
}
