using System.Collections.Generic;

namespace ServiceConnect.Interfaces.Options;

/// <summary>
/// Options for a request-reply call. Immutable readonly record struct so equality and
/// allocation behaviour match <see cref="PublishOptions"/> and <see cref="SendOptions"/>.
/// </summary>
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

    /// <summary>Single-destination override. Mutually exclusive with <see cref="EndPoints"/>.</summary>
    public string? EndPoint { get; init; }

    /// <summary>
    /// Multi-destination fan-out. Mutually exclusive with <see cref="EndPoint"/>.
    /// <see cref="List{T}"/> and any <see cref="IReadOnlyList{T}"/> implementation
    /// are assignable at construction time via an object initialiser.
    /// </summary>
    public IReadOnlyList<string>? EndPoints { get; init; }

    /// <summary>Per-call timeout in milliseconds. Defaults to <see cref="DefaultTimeoutMs"/>.</summary>
    public int Timeout { get; init; }

    /// <summary>
    /// Expected reply count. <c>null</c> falls back to <see cref="EndPoints"/>.Count when set,
    /// or single-reply behaviour when not.
    /// <list type="bullet">
    /// <item><description>
    /// <b>Positive value</b> — the call completes as soon as that many replies
    /// have arrived, or when <see cref="Timeout"/> elapses (whichever happens first).
    /// </description></item>
    /// <item><description>
    /// <b>Zero or negative</b> — the call always waits the full <see cref="Timeout"/>
    /// and returns every reply received during the window.
    /// </description></item>
    /// <item><description>
    /// <b><c>null</c> (default)</b> — falls back to <see cref="EndPoints"/>.Count if
    /// <see cref="EndPoints"/> is set; otherwise behaves as the negative case (timeout-only).
    /// </description></item>
    /// </list>
    /// </summary>
    public int? ExpectedReplyCount { get; init; }

    /// <summary>Default options instance — equivalent to <c>new RequestOptions()</c>.</summary>
    public static RequestOptions Default => new();
}
