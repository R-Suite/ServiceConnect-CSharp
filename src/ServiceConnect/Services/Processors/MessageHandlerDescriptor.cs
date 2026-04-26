using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed record MessageHandlerDescriptor(
    Type MessageType,
    Type HandlerInterfaceType,
    Action<object, IConsumeContext> SetContext,
    Func<object, object, CancellationToken, Task> InvokeHandleAsync);
