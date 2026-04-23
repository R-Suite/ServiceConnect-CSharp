namespace ServiceConnect.Services.Processors;

internal sealed record AggregatorDescriptor(
    Type MessageType,
    Type AggregatorBaseType,
    string AggregatorName,
    int BatchSize,
    TimeSpan Timeout,
    Func<IList<object>, System.Collections.IList> BuildTypedList,
    Func<object, object, CancellationToken, Task> InvokeExecuteAsync);
