using System.Diagnostics;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

/// <summary>
/// W3C trace-context propagation: extracts traceparent/tracestate from inbound headers,
/// injects them into outbound headers, and provides an AsyncLocal-backed inbound-fallback
/// so a configuration with publish telemetry enabled but consume telemetry disabled can
/// still continue the trace across the broker.
/// </summary>
internal static class TraceContextPropagation
{
    // W3C field names. DistributedContextPropagator uses these for its W3C propagator,
    // which is the default and effectively standard. Hard-coding here keeps the fallback
    // path independent of the propagator instance — if a user installs a non-W3C
    // propagator, the fallback still emits W3C, which is the dominant on-wire format.
    private const string TraceParentHeaderName = "traceparent";
    private const string TraceStateHeaderName = "tracestate";

    private static readonly AsyncLocal<InboundTraceSnapshot?> _inboundTraceFallback = new();

    private static int _warnedAboutCarrierShape;

    internal readonly record struct InboundTraceSnapshot(string TraceParent, string? TraceState);

    private sealed class InboundTraceFallbackScope(InboundTraceSnapshot? prior) : IDisposable
    {
        private InboundTraceSnapshot? _prior = prior;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _inboundTraceFallback.Value = _prior;
            _prior = default;
        }
    }

    /// <summary>
    /// Attempts to parse a W3C trace context from the supplied headers. Returns
    /// <c>true</c> and populates <paramref name="context"/> when the headers contain
    /// a well-formed traceparent; otherwise returns <c>false</c>.
    /// </summary>
    internal static bool TryGetExistingContext(IDictionary<string, string> headers, out ActivityContext context)
    {
        if (headers == null)
        {
            context = default;
            return false;
        }

        DistributedContextPropagator.Current.ExtractTraceIdAndState(
            headers, ExtractTraceIdAndState,
            out string? traceParent, out string? traceState);
        return ActivityContext.TryParse(traceParent, traceState, out context);
    }

    /// <summary>
    /// Probes the carrier headers for a "traceparent" key in the same dictionary shapes
    /// <see cref="ExtractTraceIdAndState"/> understands. Used to distinguish "no traceparent
    /// on the wire" from "traceparent present but rejected by the W3C propagator" — both
    /// surface as a null traceId at the propagator boundary, but only the second deserves
    /// a malformed-header diagnostic and a forced fresh trace root.
    /// </summary>
    internal static bool HasTraceparentHeader(object? headers) => headers switch
    {
        IDictionary<string, object> objHeaders => objHeaders.ContainsKey(TraceParentHeaderName),
        IReadOnlyDictionary<string, object> roObjHeaders => roObjHeaders.ContainsKey(TraceParentHeaderName),
        IDictionary<string, string> strHeaders => strHeaders.ContainsKey(TraceParentHeaderName),
        IReadOnlyDictionary<string, string> roStrHeaders => roStrHeaders.ContainsKey(TraceParentHeaderName),
        _ => false,
    };

    internal static void ExtractTraceIdAndState(object? eventArgs, string name, out string? value, out IEnumerable<string>? values)
    {
        values = default;

        // Iterate via the interface, not concrete Dictionary<,>. ConsumeContext
        // wraps headers as ReadOnlyDictionary<string, object>, which the old
        // concrete-type switch did not recognise — so every consume span arrived
        // without its traceparent and restarted the trace. Check the object
        // variant first (matches the raw header bag off the wire) then fall
        // back to a string-keyed dictionary for already-decoded headers.
        switch (eventArgs)
        {
            case IDictionary<string, object> objHeaders when objHeaders.TryGetValue(name, out object? objVal):
                value = HeaderDecoder.Decode(objVal);
                return;
            case IReadOnlyDictionary<string, object> roObjHeaders when roObjHeaders.TryGetValue(name, out object? roObjVal):
                value = HeaderDecoder.Decode(roObjVal);
                return;
            // string branch: values are already decoded; HeaderDecoder.Decode is for byte[] RabbitMQ headers only.
            case IDictionary<string, string> strHeaders when strHeaders.TryGetValue(name, out string? strVal):
                value = strVal;
                return;
            case IReadOnlyDictionary<string, string> roStrHeaders when roStrHeaders.TryGetValue(name, out string? roStrVal):
                value = roStrVal;
                return;
            default:
                value = default;
                return;
        }
    }

    /// <summary>
    /// Writes the current activity's W3C trace context into the outgoing-headers dictionary
    /// so downstream consumers can link their consume span to the originating publish. Mirrors
    /// <see cref="ServiceConnectActivitySource.Consume"/>'s extract side; without injection,
    /// each consume span becomes a new trace root and the end-to-end graph cannot be stitched
    /// across the broker.
    /// </summary>
    /// <remarks>
    /// When <see cref="Activity.Current"/> is null, falls back to the inbound-context snapshot
    /// stashed by <see cref="TelemetryProcessingMiddleware"/> when consume telemetry is disabled
    /// but publish telemetry is enabled. Without that fallback, a configuration of
    /// <c>EnableConsumeTelemetry=false ∧ EnablePublishTelemetry=true</c> would silently snap
    /// the trace at every consume hop because the consume side never produces an
    /// <c>Activity.Current</c> for the publish side to read.
    /// </remarks>
    internal static void InjectTraceContext(Activity? activity, IDictionary<string, string> headers)
    {
        if (activity is not null)
        {
            DistributedContextPropagator.Current.Inject(activity, headers, InjectHeader);
            return;
        }

        if (_inboundTraceFallback.Value is { } fallback)
        {
            // Pass through the original publisher's traceparent verbatim. Downstream
            // consumers see the original publisher as their parent, skipping our
            // untraced consume hop — preferable to starting a fresh trace.
            headers[TraceParentHeaderName] = fallback.TraceParent;
            if (!string.IsNullOrEmpty(fallback.TraceState))
            {
                headers[TraceStateHeaderName] = fallback.TraceState!;
            }
        }
    }

    /// <summary>
    /// Stashes the inbound traceparent/tracestate so a publish on the same logical message
    /// flow can continue the trace even when consume telemetry is disabled. The middleware
    /// must clear the value in a finally to avoid bleeding context into unrelated work.
    /// </summary>
    internal static IDisposable SetInboundTraceFallback(string traceParent, string? traceState)
    {
        var prior = _inboundTraceFallback.Value;
        _inboundTraceFallback.Value = new InboundTraceSnapshot(traceParent, traceState);
        return new InboundTraceFallbackScope(prior);
    }

    internal static ActivityContext TryResolveFallbackParentContext()
    {
        if (_inboundTraceFallback.Value is not { } fallback)
        {
            return default;
        }
        if (!ActivityContext.TryParse(fallback.TraceParent, fallback.TraceState, out var context))
        {
            return default;
        }
        return context;
    }

    internal static void InjectHeader(object? carrier, string fieldName, string fieldValue)
    {
        if (carrier is IDictionary<string, string> headers)
        {
            headers[fieldName] = fieldValue;
            return;
        }

        if (Interlocked.CompareExchange(ref _warnedAboutCarrierShape, 1, 0) == 0)
        {
            // Once-per-process diagnostic — a refactor that changes the carrier type
            // silently disables trace propagation. Use Trace because static helpers
            // don't have an ILogger; OTel users routinely route .NET trace listeners.
            Trace.TraceWarning(
                "ServiceConnectActivitySource.InjectHeader: unsupported carrier type {0}; trace context not propagated.",
                carrier?.GetType().FullName ?? "<null>");
        }
    }

    internal static void InvokeInjectHeaderForTest(object? carrier, string fieldName, string fieldValue) =>
        InjectHeader(carrier, fieldName, fieldValue);

    internal static void ResetCarrierWarnedFlagForTest() =>
        Interlocked.Exchange(ref _warnedAboutCarrierShape, 0);
}
