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
    /// Reserved for a future enhancement that routes <c>SendRequestAsync</c>,
    /// <c>SendRequestMultiAsync</c>, and <c>PublishRequestAsync</c> through the
    /// send pipeline. Not produced today.
    /// </summary>
    Request,
}
