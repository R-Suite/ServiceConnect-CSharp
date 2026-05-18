namespace ServiceConnect.Services;

/// <summary>
/// AsyncLocal-backed accessor for the headers of the inbound message currently being dispatched.
/// Used by outbound paths (Bus.RouteAsync, middleware) to read the inbound hop counter and
/// other carried context. Implementations must be safe for concurrent reads across messages
/// (the AsyncLocal's per-flow value isolates them).
/// </summary>
internal interface IConsumeContextAccessor
{
    /// <summary>The current inbound headers, or <see langword="null"/> when no consume flow is active.</summary>
    IReadOnlyDictionary<string, object>? CurrentHeaders { get; }

    /// <summary>
    /// Pushes a headers view as the current flow's context. The returned <see cref="IDisposable"/>
    /// restores the previous value when disposed; supports nested pushes.
    /// </summary>
    IDisposable Push(IReadOnlyDictionary<string, object> headers);
}
