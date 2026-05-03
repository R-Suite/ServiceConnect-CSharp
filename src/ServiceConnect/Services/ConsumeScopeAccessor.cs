using Microsoft.Extensions.DependencyInjection;

namespace ServiceConnect.Services;

/// <summary>
/// Flows the current per-message dependency-injection scope through AsyncLocal so that
/// inbound filters, middleware, and processors can resolve scoped services from the
/// same container scope the dispatcher established for the message. The outbound filter
/// path in <see cref="Bus"/> pushes a fresh scope around each call for the same reason.
/// </summary>
public sealed class ConsumeScopeAccessor
{
    // Instance-scoped AsyncLocal so multiple ConsumeScopeAccessor instances in the same
    // AppDomain (e.g. two Bus instances) maintain independent scopes. Pre-Phase-11 this
    // was a static field, leaking scopes across bus boundaries.
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
