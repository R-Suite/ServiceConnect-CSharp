namespace ServiceConnect.Interfaces;

public sealed class ConsumeEventResult
{
    public bool Success { get; set; }
    public Exception? Exception { get; set; }
}
