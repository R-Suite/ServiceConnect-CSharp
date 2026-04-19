namespace ServiceConnect.Interfaces;

public sealed class ConsumeEventArgs
{
    public byte[] Message { get; init; } = Array.Empty<byte>();

    public string Type { get; init; } = string.Empty;

    public IDictionary<string, object> Headers
    {
        // Lazy getter: backing field is null! when ConsumeEventArgs is constructed without
        // setting Headers — avoids the wasted allocation from the field initializer.
        get => _headers ??= new Dictionary<string, object>();
        init => _headers = value ?? new Dictionary<string, object>();
    }

    private IDictionary<string, object> _headers = null!;
}
