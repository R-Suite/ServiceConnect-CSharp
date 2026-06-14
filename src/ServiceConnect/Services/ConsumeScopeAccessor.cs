using Microsoft.Extensions.DependencyInjection;

namespace ServiceConnect.Services;

/// <summary>
/// Flows the current per-message dependency-injection scope through AsyncLocal so that
/// inbound filters, middleware, and processors can resolve scoped services from the
/// same container scope the dispatcher established for the message. The outbound filter
/// path in <see cref="Bus"/> pushes a fresh scope around each call for the same reason.
/// </summary>
/// <remarks>
/// <b>Fire-and-forget reader hazard.</b> The accessor uses <see cref="AsyncLocal{T}"/>,
/// which propagates writes only along the current <see cref="ExecutionContext"/>. A
/// handler that starts a fire-and-forget task while the scope is live captures the
/// ambient context at that point — the captured task continues to see the pushed
/// scope after the using-block disposes. The dispatcher disposes the underlying DI
/// scope on return, so a leaked continuation that reads <see cref="Current"/> will
/// observe an <see cref="IServiceProvider"/> whose backing scope has been disposed;
/// subsequent <c>GetService</c> calls throw <see cref="ObjectDisposedException"/>.
/// <para>
/// <b>Rule for handler authors:</b> never read <see cref="Current"/> from a task that
/// outlives the handler's awaited completion. Capture any required scoped service
/// into a local before starting fire-and-forget work.
/// </para>
/// </remarks>
internal sealed class ConsumeScopeAccessor : IConsumeScopeAccessor
{
    // Instance-scoped AsyncLocal so multiple ConsumeScopeAccessor instances in the same
    // AppDomain (e.g. two Bus instances) maintain independent scopes. A static AsyncLocal
    // here would leak scopes across bus boundaries.
    private readonly AsyncLocal<IServiceProvider?> _current = new();

    /// <summary>
    /// Pushes <paramref name="serviceProvider"/> as the current scope. The returned
    /// disposable restores the previous value, supporting nested pushes.
    /// </summary>
    public IDisposable Push(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        var previous = _current.Value;
        _current.Value = serviceProvider;
        return new Popper(this, previous);
    }

    /// <summary>
    /// Returns the current scope's <see cref="IServiceProvider"/>. Throws when no scope
    /// is pushed — callers must establish a scope (via <see cref="Push"/>) before use.
    /// </summary>
    public IServiceProvider Current =>
        _current.Value ?? throw new InvalidOperationException(
            "No consume scope is currently active. The dispatcher and outbound filter path must push a scope before resolving scoped services.");

    private sealed class Popper(ConsumeScopeAccessor outer, IServiceProvider? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            outer._current.Value = previous;
        }
    }
}
