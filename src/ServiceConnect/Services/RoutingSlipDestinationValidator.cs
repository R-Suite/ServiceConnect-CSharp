using System.Runtime.CompilerServices;

namespace ServiceConnect.Services;

/// <summary>
/// Shared validator for routing-slip destination queue names. Used both at send-time
/// (Bus.RouteAsync) so producers fail fast with a typed argument error, and at
/// receive-time (HandlerProcessor.ForwardRoutingSlipAsync) as a defence-in-depth check
/// against attacker-controlled RoutingSlip headers redirecting traffic.
/// </summary>
internal static class RoutingSlipDestinationValidator
{
    public const int MaxDestinationLength = 128;

    // Characters either structural in AMQP routing (`*`, `#` are wildcards on topic
    // exchanges) or common injection vectors (`\0`, `\r`, `\n`, `\t`, quotes).
    public static readonly char[] ForbiddenChars = ['*', '#', '\0', '\r', '\n', '\t', '"', '\''];

    /// <summary>
    /// Returns the failure reason as a string when invalid, or <see langword="null"/>
    /// when the destination is acceptable. Caller chooses how to surface the failure
    /// (ArgumentException at send-time, log + drop at receive-time).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? GetFailureReason(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return "destination is null or whitespace";
        }
        if (destination.Length > MaxDestinationLength)
        {
            return $"destination exceeds the {MaxDestinationLength}-character cap";
        }
        if (destination.IndexOfAny(ForbiddenChars) >= 0)
        {
            return "destination contains a reserved character (one of *, #, NUL, CR, LF, TAB, \", ')";
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValid(string? destination) => GetFailureReason(destination) is null;
}
