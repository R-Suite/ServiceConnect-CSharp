using System.Buffers;

namespace ServiceConnect.Interfaces;

/// <summary>
/// Serializes and deserializes <see cref="Message"/> instances.
/// </summary>
public interface IMessageSerializer
{
    /// <summary>
    /// Serializes <paramref name="message"/> directly into <paramref name="output"/>.
    /// Implementations should write without allocating an intermediate <see cref="byte"/> array.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="message">The message to serialize.</param>
    /// <param name="output">The destination buffer writer. Caller owns its lifetime.</param>
    void Serialize<T>(T message, IBufferWriter<byte> output) where T : Message;

    /// <summary>
    /// Deserializes a message of the specified expected type from <paramref name="data"/>.
    /// </summary>
    /// <typeparam name="T">The expected message type.</typeparam>
    /// <param name="data">The serialized payload.</param>
    /// <returns>The deserialized message.</returns>
    T Deserialize<T>(ReadOnlyMemory<byte> data) where T : Message;

    /// <summary>
    /// Deserializes a message of the runtime-supplied <paramref name="type"/>.
    /// Used by the dispatch path which resolves the CLR type from the registry.
    /// </summary>
    /// <param name="data">The serialized payload.</param>
    /// <param name="type">The destination CLR type.</param>
    /// <returns>The deserialized object.</returns>
    object Deserialize(ReadOnlyMemory<byte> data, Type type);

    /// <summary>
    /// Deserializes a message from a (possibly multi-segment) <see cref="ReadOnlySequence{T}"/>.
    /// Used by the streaming path where buffered packets are stitched as a sequence rather than
    /// copied into a contiguous buffer.
    /// </summary>
    /// <remarks>
    /// The default implementation flattens the sequence into a <see cref="byte"/>[] before
    /// delegating to <see cref="Deserialize(ReadOnlyMemory{byte}, Type)"/>, which allocates a
    /// copy. Implementations that can read across segments without flattening — e.g. via
    /// <c>System.Text.Json.Utf8JsonReader</c> on a sequence — should override to avoid the
    /// allocation on multi-segment input.
    /// </remarks>
    /// <param name="data">The serialized payload, possibly spanning multiple segments.</param>
    /// <param name="type">The destination CLR type.</param>
    /// <returns>The deserialized object.</returns>
    object Deserialize(in ReadOnlySequence<byte> data, Type type)
        => Deserialize(BuffersExtensions.ToArray(data), type);
}
