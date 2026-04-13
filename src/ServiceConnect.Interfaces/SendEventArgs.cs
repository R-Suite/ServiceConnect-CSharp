namespace ServiceConnect.Interfaces;

public sealed class SendEventArgs : OutgoingEventArgs
{
    public string EndPoint { get; init; } = string.Empty;

    private IReadOnlyList<string>? _endPointsCached;
    // Parsed list cached on first access (P-07/P-41). Wire-format kept in EndPoint
    // for backwards compatibility with existing consumers.
    public IReadOnlyList<string> EndPoints
    {
        get => _endPointsCached ??= EndPoint.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries);
        init
        {
            EndPoint = "[" + string.Join(',', value) + "]";
            _endPointsCached = value as IReadOnlyList<string> ?? value.ToArray();
        }
    }
}