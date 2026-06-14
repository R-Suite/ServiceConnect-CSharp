using System.Threading;

namespace ServiceConnect.Services;

internal sealed class ConsumeContextAccessor : IConsumeContextAccessor
{
    private readonly AsyncLocal<IReadOnlyDictionary<string, object>?> _currentHeaders = new();

    public IReadOnlyDictionary<string, object>? CurrentHeaders => _currentHeaders.Value;

    public IDisposable Push(IReadOnlyDictionary<string, object> headers)
    {
        var previous = _currentHeaders.Value;
        _currentHeaders.Value = headers;
        return new Scope(this, previous);
    }

    private sealed class Scope(ConsumeContextAccessor owner, IReadOnlyDictionary<string, object>? previous) : IDisposable
    {
        private readonly ConsumeContextAccessor _owner = owner;
        private readonly IReadOnlyDictionary<string, object>? _previous = previous;
        private int _disposed;

        public void Dispose()
        {
            // Interlocked latch matches ConsumeScopeAccessor.Popper. Two threads disposing
            // concurrently would otherwise both write _currentHeaders.Value = previous.
            // Current call sites always wrap Push in a single `using`, so the race is
            // theoretical — but the inconsistency is a footgun for any future caller
            // disposing from a finalizer or fire-and-forget continuation.
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner._currentHeaders.Value = _previous;
        }
    }
}
