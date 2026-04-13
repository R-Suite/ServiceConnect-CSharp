using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public sealed record ProcessManagerDescriptor(
    Type MessageType,
    Type DataType,
    Type ProcessHandlerInterfaceType,
    Func<IProcessManagerData> CreateData,
    Action<IProcessManagerData, Guid> SetCorrelationId,
    Action<object, IConsumeContext> SetHandlerContext,
    Action<object, IProcessManagerPropertyMapper> ConfigureMapper,
    Func<IProcessManagerFinder, IProcessManagerPropertyMapper, Message, CancellationToken, Task<object?>> FindData,
    Func<object, object> GetPersistenceDataData,
    Func<IProcessManagerFinder, object, CancellationToken, Task> UpdateData,
    Func<object, Message, object, Task> InvokeHandleAsync);
