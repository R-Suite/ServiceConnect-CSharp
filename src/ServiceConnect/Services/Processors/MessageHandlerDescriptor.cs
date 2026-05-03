using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed record MessageHandlerDescriptor(
    Type MessageType,
    Type HandlerInterfaceType,
    Func<object, object, IConsumeContext, CancellationToken, Task> InvokeHandleAsync);
