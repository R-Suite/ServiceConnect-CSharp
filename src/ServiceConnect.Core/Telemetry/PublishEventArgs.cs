using ServiceConnect.Interfaces;
using System.Collections.Generic;

namespace ServiceConnect.Core.Telemetry;

public class PublishEventArgs
{
    public Message Message { get; init; }

    public string RoutingKey { get; init; } = string.Empty;

    public Dictionary<string, string> Headers { get; init; } = new();
}