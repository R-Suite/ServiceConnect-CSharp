namespace ServiceConnect.Interfaces;

/// <summary>
/// Serializes and deserializes <see cref="Message"/> instances.
/// </summary>
public interface IMessageSerializer
{
    /// <summary>
    /// Serializes a message into a new byte array.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="message">The message to serialize.</param>
    /// <returns>The serialized payload.</returns>
    byte[] Serialize<T>(T message) where T : Message;

    /// <summary>
    /// Serializes a message into the provided buffer writer.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="message">The message to serialize.</param>
    /// <param name="output">The destination buffer writer.</param>
    void Serialize<T>(T message, System.Buffers.IBufferWriter<byte> output) where T : Message;

    /// <summary>
    /// Deserializes a message from a byte array.
    /// </summary>
    /// <typeparam name="T">The expected message type.</typeparam>
    /// <param name="data">The serialized payload.</param>
    /// <returns>The deserialized message.</returns>
    T Deserialize<T>(byte[] data) where T : Message;

    /// <summary>
    /// Deserializes a message from a span of bytes.
    /// </summary>
    /// <typeparam name="T">The expected message type.</typeparam>
    /// <param name="data">The serialized payload.</param>
    /// <returns>The deserialized message.</returns>
    T Deserialize<T>(ReadOnlySpan<byte> data) where T : Message;

    /// <summary>
    /// Deserializes a message into the specified CLR type.
    /// </summary>
    /// <param name="data">The serialized payload.</param>
    /// <param name="type">The destination CLR type.</param>
    /// <returns>The deserialized object.</returns>
    object Deserialize(byte[] data, Type type);

    /// <summary>
    /// Deserializes a message span into the specified CLR type.
    /// </summary>
    /// <param name="data">The serialized payload.</param>
    /// <param name="type">The destination CLR type.</param>
    /// <returns>The deserialized object.</returns>
    object Deserialize(ReadOnlySpan<byte> data, Type type);

    /// <summary>
    /// Deserializes a message memory block into the specified CLR type.
    /// </summary>
    /// <param name="data">The serialized payload.</param>
    /// <param name="type">The destination CLR type.</param>
    /// <returns>The deserialized object.</returns>
    object Deserialize(ReadOnlyMemory<byte> data, Type type)
        => Deserialize(data.ToArray(), type);

    /// <summary>
    /// Deserializes a message memory block into the specified message type.
    /// </summary>
    /// <typeparam name="T">The expected message type.</typeparam>
    /// <param name="data">The serialized payload.</param>
    /// <returns>The deserialized message.</returns>
    T Deserialize<T>(ReadOnlyMemory<byte> data) where T : Message
        => (T)Deserialize(data, typeof(T));

    /// <summary>
    /// Deserializes a segmented byte sequence into the specified CLR type.
    /// </summary>
    /// <param name="data">The serialized payload.</param>
    /// <param name="type">The destination CLR type.</param>
    /// <returns>The deserialized object.</returns>
    object Deserialize(in System.Buffers.ReadOnlySequence<byte> data, Type type)
        => Deserialize(System.Buffers.BuffersExtensions.ToArray(data), type);
}
