using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

/// <summary>
/// Mutable holder for the singleton <see cref="IBus"/>, populated by the
/// <see cref="IBus"/> factory as soon as the Bus instance is constructed.
/// <para>
/// Components that need a deferred reference to <see cref="IBus"/> (typically to break a
/// circular dependency at construction time) resolve a <see cref="Lazy{T}"/> bound to
/// this accessor rather than capturing the root <see cref="IServiceProvider"/>. Capturing
/// the root provider inside a lazy factory risks deadlock when the factory is touched
/// during Bus construction — a scenario silent failures are hard to diagnose for. Reading
/// <see cref="Bus"/> before it has been set surfaces a clear error instead.
/// </para>
/// </summary>
internal sealed class BusAccessor
{
    private IBus? _bus;

    public IBus? Bus => Volatile.Read(ref _bus);

    public void Set(IBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        Volatile.Write(ref _bus, bus);
    }

    public IBus GetOrThrow()
    {
        var bus = Volatile.Read(ref _bus);
        if (bus == null)
            throw new InvalidOperationException(
                "IBus was accessed before it finished constructing. Components must not dereference Lazy<IBus>.Value during Bus construction.");
        return bus;
    }
}
