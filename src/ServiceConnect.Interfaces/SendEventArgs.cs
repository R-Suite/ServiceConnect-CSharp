namespace ServiceConnect.Interfaces;

public sealed class SendEventArgs : OutgoingEventArgs
{
    public string EndPoint { get; init; } = string.Empty;
    public IReadOnlyList<string> EndPoints { get; init; } = [];
}
