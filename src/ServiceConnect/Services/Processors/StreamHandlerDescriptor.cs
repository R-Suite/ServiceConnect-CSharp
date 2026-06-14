using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed record StreamHandlerDescriptor(
    Type MessageType,
    Type HandlerInterfaceType,
    Func<object, object, IMessageBusReadStream, CancellationToken, Task> InvokeExecuteAsync);
