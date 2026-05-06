using System.Collections.Generic;

namespace ServiceConnect.Interfaces.Options;

/// <summary>
/// Options for a request-reply call. Immutable readonly record struct so equality and
/// allocation behaviour match <see cref="PublishOptions"/> and <see cref="SendOptions"/>.
/// </summary>
/// <remarks>
/// <b>Do not pass <c>default(RequestOptions)</c>.</b> The C# language semantics of
/// <c>default</c> for a struct skip the parameterless constructor, leaving
/// <see cref="Timeout"/> at <c>0</c>. The request-reply path rejects this with
/// <see cref="ArgumentOutOfRangeException"/> rather than silently expiring after 0 ms.
/// Use <see cref="Default"/> or <c>new RequestOptions()</c> instead.
/// </remarks>
public readonly record struct RequestOptions
{
    /// <summary>Default per-call timeout in milliseconds.</summary>
    public const int DefaultTimeoutMs = 10_000;

    /// <summary>
    /// Initialises <see cref="Timeout"/> to <see cref="DefaultTimeoutMs"/>.
    /// All other properties default to <c>null</c>.
    /// </summary>
    public RequestOptions()
    {
        Timeout = DefaultTimeoutMs;
    }

    /// <summary>Optional headers added to the outbound request envelope.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Single-destination override for the request.</summary>
    public string? EndPoint { get; init; }

    /// <summary>Per-call timeout in milliseconds. Defaults to <see cref="DefaultTimeoutMs"/>.</summary>
    public int Timeout { get; init; }

    /// <summary>
    /// Expected reply count.
    /// <list type="bullet">
    /// <item><description>
    /// <b>Positive value</b> — the call completes as soon as that many replies have arrived,
    /// or when <see cref="Timeout"/> elapses (whichever happens first).
    /// </description></item>
    /// <item><description>
    /// <b>Zero, negative, or null (default)</b> — the call always waits the full
    /// <see cref="Timeout"/> and returns every reply received during the window.
    /// </description></item>
    /// </list>
    /// </summary>
    public int? ExpectedReplyCount { get; init; }

    /// <summary>Default options instance — equivalent to <c>new RequestOptions()</c>.</summary>
    public static RequestOptions Default => new();
}
