using System.Threading;

namespace ServiceConnect.Services;

internal sealed class ConsumeContextAccessor
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
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _owner._currentHeaders.Value = _previous;
            _disposed = true;
        }
    }
}
