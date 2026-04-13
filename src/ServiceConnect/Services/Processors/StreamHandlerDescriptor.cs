using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed record StreamHandlerDescriptor(
    Type MessageType,
    Type HandlerInterfaceType,
    Action<object, IMessageBusReadStream> SetStream,
    Action<object, object> InvokeExecute);
