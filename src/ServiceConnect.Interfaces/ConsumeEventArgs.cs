using System;
using System.Collections.Generic;

namespace ServiceConnect.Interfaces;

public class ConsumeEventArgs
{
    public byte[] Message { get; init; } = Array.Empty<byte>();

    public string Type { get; init; } = string.Empty;

    public IDictionary<string, object> Headers
    {
        get => _headers;
        init => _headers = value is not null ? value : new Dictionary<string, object>();
    }

    private IDictionary<string, object> _headers = new Dictionary<string, object>();
}