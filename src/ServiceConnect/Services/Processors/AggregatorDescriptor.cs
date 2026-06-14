namespace ServiceConnect.Services.Processors;

internal sealed record AggregatorDescriptor(
    Type MessageType,
    Type AggregatorBaseType,
    string AggregatorName,
    int BatchSize,
    TimeSpan Timeout,
    // Returns an IReadOnlyList<TConcreteMessageType> boxed as object. The concrete type is
    // determined at registry build time by CompileBuildTypedList. Callers must cast to
    // IReadOnlyList<TConcreteMessageType>; a wrong cast fails at dispatch with InvalidCastException
    // rather than silently accepting a non-IReadOnlyList<T> IList.
    Func<IList<object>, object> BuildTypedList,
    Func<object, object, CancellationToken, Task> InvokeExecuteAsync);
