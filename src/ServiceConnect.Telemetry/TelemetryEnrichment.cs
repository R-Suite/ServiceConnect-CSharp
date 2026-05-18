using System.Diagnostics;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

/// <summary>
/// Invokes user-supplied enrichment callbacks against created activities. Catches and
/// records enrichment exceptions as a tag rather than failing the dispatch.
/// </summary>
internal static class TelemetryEnrichment
{
    /// <summary>
    /// Invokes <see cref="ServiceConnectInstrumentationOptions.EnrichWithMessage"/>
    /// against the activity. Swallows non-OCE exceptions and records the type name
    /// on the activity as <c>enrichment.exception</c>. OCE is rethrown so callers can
    /// distinguish cancellation from enrichment failure.
    /// </summary>
    internal static void TryEnrich(Activity activity, Message? message, ServiceConnectInstrumentationOptions options)
    {
        if (message is null)
        {
            return;
        }

        try
        {
            options.EnrichWithMessage?.Invoke(activity, message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Tag the exception type only. Message strings can contain caller-controlled
            // payloads or PII; the type name is sufficient diagnostic.
            activity.SetTag("enrichment.exception", ex.GetType().FullName);
        }
    }

    /// <summary>
    /// Bytes overload of the enrichment helper. Same semantics as the
    /// <see cref="Message"/> overload: OCE rethrown, other exceptions tagged.
    /// </summary>
    internal static void TryEnrich(Activity activity, byte[]? message, ServiceConnectInstrumentationOptions options)
    {
        if (message is null)
        {
            return;
        }

        try
        {
            options.EnrichWithMessageBytes?.Invoke(activity, message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity.SetTag("enrichment.exception", ex.GetType().FullName);
        }
    }
}
