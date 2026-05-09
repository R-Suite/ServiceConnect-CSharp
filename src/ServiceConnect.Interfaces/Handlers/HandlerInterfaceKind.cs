namespace ServiceConnect.Interfaces;

/// <summary>
/// Discriminates which handler interface a <see cref="HandlerReference"/> was produced for.
/// A single class that implements multiple handler interfaces produces one reference per interface.
/// </summary>
public enum HandlerInterfaceKind
{
    /// <summary>
    /// The handler implements <see cref="IMessageHandler{TMessage}"/>.
    /// </summary>
    MessageHandler,

    /// <summary>
    /// The handler implements <see cref="IProcessHandler{TData,TMessage}"/>.
    /// </summary>
    ProcessHandler,

    /// <summary>
    /// The handler implements <see cref="IStreamHandler{TMessage}"/>.
    /// </summary>
    StreamHandler,

    /// <summary>
    /// The handler extends <see cref="Aggregator{TMessage}"/>.
    /// </summary>
    Aggregator,
}
