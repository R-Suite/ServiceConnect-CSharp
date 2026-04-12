namespace ServiceConnect.Interfaces;

public class ConsumeEventResult
{
    public bool Success { get; set; }
    public Exception? Exception { get; set; }
}
