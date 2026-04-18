namespace ServiceConnect.Interfaces.Options;

public sealed class RequestOptions
{
    public const int DefaultTimeoutMs = 10_000;
    public static RequestOptions Default { get; } = new();

    public Dictionary<string, string>? Headers { get; set; }
    public string? EndPoint { get; set; }
    public IList<string>? EndPoints { get; set; }
    public int Timeout { get; set; } = DefaultTimeoutMs;

    /// <summary>
    /// Number of replies the multi-request should wait for before completing
    /// (only used by <c>SendRequestMultiAsync</c>).
    /// </summary>
    /// <remarks>
    /// Behaviour:
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
    /// <see cref="EndPoints"/> is set; otherwise behaves as the negative case
    /// (timeout-only).
    /// </description></item>
    /// </list>
    /// </remarks>
    public int? ExpectedReplyCount { get; set; }
}
