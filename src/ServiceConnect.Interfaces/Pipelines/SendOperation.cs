namespace ServiceConnect.Interfaces;

/// <summary>
/// Categorises the call site that produced a <see cref="SendContext"/>.
/// </summary>
public enum SendOperation
{
    /// <summary>Originated from <see cref="IBus.PublishAsync{T}"/>.</summary>
    Publish,

    /// <summary>Originated from <see cref="IBus.SendAsync{T}"/>.</summary>
    Send,

    /// <summary>
    /// Originated from <see cref="IBus.SendRequestAsync{TRequest, TReply}"/>,
    /// <see cref="IBus.SendRequestMultiAsync{TRequest, TReply}"/>, or
    /// <see cref="IBus.PublishRequestAsync{TRequest, TReply}"/>.
    /// </summary>
    Request,
}
