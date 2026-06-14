namespace ServiceConnect.Services;

/// <summary>
/// AsyncLocal-backed accessor for the current per-message DI scope's <see cref="IServiceProvider"/>.
/// Lets filters/middleware/processors resolve scoped services from the same scope the
/// dispatcher established for the inbound message.
/// </summary>
internal interface IConsumeScopeAccessor
{
    /// <summary>The current scope's <see cref="IServiceProvider"/>. Throws when no scope is pushed.</summary>
    IServiceProvider Current { get; }

    /// <summary>Pushes a scope; returned <see cref="IDisposable"/> restores the previous value.</summary>
    IDisposable Push(IServiceProvider serviceProvider);
}
