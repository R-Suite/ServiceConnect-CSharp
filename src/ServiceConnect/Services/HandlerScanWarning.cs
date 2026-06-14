namespace ServiceConnect.Services;

/// <summary>
/// A partial-scan warning captured during handler discovery. Registered as a DI singleton
/// list so the warnings can be replayed against the configured logger at host startup —
/// scan runs at DI-registration time before an <c>ILoggerFactory</c> is available, so
/// without this side-channel the warnings would be dropped to <c>NullLogger.Instance</c>.
/// </summary>
/// <param name="AssemblyName">The full name of the assembly that failed (or "&lt;unknown&gt;").</param>
/// <param name="ExceptionType">Short name of the thrown exception type.</param>
/// <param name="Detail">Human-readable description of the partial scan outcome.</param>
/// <param name="Exception">The original exception for log-record attachment.</param>
internal sealed record HandlerScanWarning(
    string AssemblyName,
    string ExceptionType,
    string Detail,
    Exception Exception);
